using System.Collections.Concurrent;
using System.Threading.Channels;
using Npgsql;

namespace AgentHub.Api.Events;

/// <summary>
/// What changed about a session. The wire carries the kind and nothing else — a subscriber
/// reacts by re-reading the REST endpoint it already uses, which is where the authorization
/// filtering lives. Shipping the changed state over the socket instead would mean a second
/// place that has to decide what a shared-link viewer may see.
/// </summary>
public static class SessionEventKinds
{
    public const string Files = "files";
}

public interface ISessionEventBus
{
    Task PublishAsync(string sessionId, string kind, CancellationToken ct = default);

    /// <summary>
    /// Events for one session, from the moment of the call. Dispose to stop listening; failing
    /// to do so leaks a channel for the lifetime of the process.
    /// </summary>
    ISessionEventSubscription Subscribe(string sessionId);
}

public interface ISessionEventSubscription : IDisposable
{
    IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct);
}

/// <summary>
/// Cross-replica fan-out over Postgres LISTEN/NOTIFY.
///
/// The backend runs with more than one replica, so the write that changes a session and the
/// socket that has to hear about it routinely land on different pods — an in-process event
/// bus would deliver to the wrong one roughly half the time. Postgres is already a hard
/// dependency and NOTIFY is delivered on commit, which also means a rolled-back write
/// correctly produces no event. The alternative, a Redis backplane (what SignalR would want),
/// buys nothing here and adds a component to operate.
/// </summary>
public sealed class PostgresSessionEventBus : ISessionEventBus, IHostedService, IAsyncDisposable
{
    public const string Channel = "agenthub_session_events";

    // Bounded so one stalled socket cannot grow without limit, and dropping the oldest is
    // harmless: every message says "re-read", so a coalesced burst and a single event lead the
    // subscriber to the same place.
    private const int SubscriberCapacity = 32;

    private readonly ConcurrentDictionary<string, ConcurrentDictionary<Guid, Channel<string>>> _subscribers =
        new(StringComparer.Ordinal);
    private readonly NpgsqlDataSource _db;
    private readonly string _connectionString;
    private readonly ILogger<PostgresSessionEventBus> _log;
    private readonly TaskCompletionSource _listenEstablished =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
    private CancellationTokenSource? _listening;
    private Task? _listener;
    private int _connects;

    public PostgresSessionEventBus(IConfiguration configuration, ILogger<PostgresSessionEventBus> log)
    {
        _connectionString = configuration.GetConnectionString("Postgres")
            ?? throw new InvalidOperationException("ConnectionStrings:Postgres is missing.");
        _db = NpgsqlDataSource.Create(_connectionString);
        _log = log;
    }

    public async Task PublishAsync(string sessionId, string kind, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(sessionId) || string.IsNullOrWhiteSpace(kind)) return;
        await using var command = _db.CreateCommand("SELECT pg_notify(@channel, @payload)");
        command.Parameters.AddWithValue("channel", Channel);
        command.Parameters.AddWithValue("payload", Encode(sessionId, kind));
        await command.ExecuteNonQueryAsync(ct);
    }

    public ISessionEventSubscription Subscribe(string sessionId)
    {
        var channel = System.Threading.Channels.Channel.CreateBounded<string>(
            new BoundedChannelOptions(SubscriberCapacity)
            {
                FullMode = BoundedChannelFullMode.DropOldest,
                SingleReader = true
            });
        var id = Guid.NewGuid();
        _subscribers.GetOrAdd(sessionId, _ => new ConcurrentDictionary<Guid, Channel<string>>())[id] = channel;
        return new Subscription(this, sessionId, id, channel);
    }

    public async Task StartAsync(CancellationToken ct)
    {
        _listening = new CancellationTokenSource();
        _listener = ListenLoopAsync(_listening.Token);
        // Returning before LISTEN is live would leave a window in which a subscriber attaches,
        // a write commits, and the notification is simply not being listened for. Bounded,
        // because a database that is slow to accept connections should delay startup, not
        // block it: the retry loop owns recovery from there, and its resync covers whoever
        // attached in the meantime.
        await Task.WhenAny(_listenEstablished.Task, Task.Delay(TimeSpan.FromSeconds(10), ct));
    }

    public async Task StopAsync(CancellationToken ct)
    {
        if (_listening is null) return;
        await _listening.CancelAsync();
        if (_listener is not null)
        {
            try
            {
                await _listener.WaitAsync(ct);
            }
            catch (OperationCanceledException)
            {
                // Shutdown ran out of time before the loop unwound. The connection goes with the
                // process either way, so this does not block stopping — but a host that reaches
                // this on every shutdown has a loop that is not observing its token.
                _log.LogDebug("Session event listener did not stop within the shutdown timeout");
            }
        }
    }

    /// <summary>
    /// One dedicated connection per replica, reconnecting with backoff. A pooled connection is
    /// the wrong tool: LISTEN registers against the physical session, so returning it to the
    /// pool would silently unsubscribe us while leaving the registration behind for whoever
    /// borrows it next.
    /// </summary>
    private async Task ListenLoopAsync(CancellationToken ct)
    {
        var attempt = 0;
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await using var connection = new NpgsqlConnection(_connectionString);
                connection.Notification += OnNotification;
                await connection.OpenAsync(ct);
                await using (var listen = new NpgsqlCommand($"LISTEN {Channel}", connection))
                {
                    await listen.ExecuteNonQueryAsync(ct);
                }

                attempt = 0;
                // Only from the second connect onwards. A reconnect means notifications were
                // missed while we were away, so everyone has to re-read; the first connect has
                // no gap behind it, and resyncing there would emit an event indistinguishable
                // from a real one — which is precisely what let a broken trigger look healthy.
                if (Interlocked.Increment(ref _connects) > 1) ResyncAll();
                _listenEstablished.TrySetResult();
                while (!ct.IsCancellationRequested) await connection.WaitAsync(ct);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
            catch (Exception error)
            {
                var delay = TimeSpan.FromMilliseconds(Math.Min(30_000, 500 * Math.Pow(2, attempt++)));
                _log.LogWarning(error,
                    "Session event listener dropped; reconnecting in {Delay}", delay);
                try { await Task.Delay(delay, ct); } catch (OperationCanceledException) { return; }
            }
        }
    }

    private void OnNotification(object sender, NpgsqlNotificationEventArgs args)
    {
        if (!TryDecode(args.Payload, out var sessionId, out var kind)) return;
        if (!_subscribers.TryGetValue(sessionId, out var listeners)) return;
        foreach (var listener in listeners.Values) listener.Writer.TryWrite(kind);
    }

    private void ResyncAll()
    {
        foreach (var listeners in _subscribers.Values)
        {
            foreach (var listener in listeners.Values) listener.Writer.TryWrite(SessionEventKinds.Files);
        }
    }

    private void Unsubscribe(string sessionId, Guid id)
    {
        if (!_subscribers.TryGetValue(sessionId, out var listeners)) return;
        listeners.TryRemove(id, out _);
        // Racy by nature: a subscriber may attach between the emptiness check and the removal.
        // Re-adding the bucket on the next Subscribe is correct, so the worst case is a wasted
        // dictionary entry, never a lost delivery.
        if (listeners.IsEmpty) _subscribers.TryRemove(sessionId, out _);
    }

    // Public because this is the wire contract between replicas, not an implementation detail:
    // a pod running an older build decodes what a newer one encoded. A newline separates the
    // two halves since neither a session id nor a kind can contain one.
    public static string Encode(string sessionId, string kind) => $"{sessionId}\n{kind}";

    public static bool TryDecode(string? payload, out string sessionId, out string kind)
    {
        sessionId = string.Empty;
        kind = string.Empty;
        if (string.IsNullOrEmpty(payload)) return false;
        var split = payload.IndexOf('\n');
        if (split <= 0 || split == payload.Length - 1) return false;
        sessionId = payload[..split];
        kind = payload[(split + 1)..];
        return true;
    }

    public async ValueTask DisposeAsync()
    {
        // Stop first. Disposing a CancellationTokenSource does not cancel it, so a bus disposed
        // without StopAsync would leave the listen loop waiting on a connection whose data
        // source is being torn down underneath it — holding a database connection open for the
        // rest of the process's life.
        await StopAsync(CancellationToken.None);
        _listening?.Dispose();
        await _db.DisposeAsync();
    }

    private sealed class Subscription : ISessionEventSubscription
    {
        private readonly PostgresSessionEventBus _bus;
        private readonly string _sessionId;
        private readonly Guid _id;
        private readonly Channel<string> _channel;
        private bool _disposed;

        public Subscription(PostgresSessionEventBus bus, string sessionId, Guid id, Channel<string> channel)
        {
            _bus = bus;
            _sessionId = sessionId;
            _id = id;
            _channel = channel;
        }

        public IAsyncEnumerable<string> ReadAllAsync(CancellationToken ct) =>
            _channel.Reader.ReadAllAsync(ct);

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            _bus.Unsubscribe(_sessionId, _id);
            _channel.Writer.TryComplete();
        }
    }
}
