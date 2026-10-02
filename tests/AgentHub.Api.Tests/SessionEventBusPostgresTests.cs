using AgentHub.Api.Events;
using AgentHub.Api.Files;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// These run against a real server on purpose. The mechanism under test is a plpgsql trigger
/// plus Npgsql's LISTEN handling — a fake bus would assert that the test's own stub works and
/// would have passed just as happily with no trigger installed at all.
/// </summary>
public sealed class SessionEventBusPostgresTests
{
    // Generous: it covers a scheduling hiccup on a loaded CI box, and a working delivery
    // returns in milliseconds, so the wait only costs anything when the test is already failing.
    private static readonly TimeSpan Delivery = TimeSpan.FromSeconds(10);

    [PostgreSqlFact]
    public async Task A_file_write_wakes_a_subscriber_without_any_publish_call()
    {
        await using var db = await SessionFilePostgresFixture.CreateAsync();
        await using var bus = CreateBus(db.ConnectionString);
        await bus.StartAsync(CancellationToken.None);

        // Unique per run: NOTIFY channels are database-wide, not schema-scoped, so a test
        // running beside this one publishes onto the same channel.
        var sessionId = $"s-{Guid.NewGuid():N}";
        using var subscription = bus.Subscribe(sessionId);
        using var cts = new CancellationTokenSource(Delivery);
        var events = subscription.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        // Nothing here publishes: InsertAsync is the ordinary write path, and the trigger is
        // what has to turn it into an event.
        await db.Registry.InsertAsync(Ready("f1", sessionId));

        Assert.True(await events.MoveNextAsync());
        Assert.Equal(SessionEventKinds.Files, events.Current);
        await events.DisposeAsync();
        await bus.StopAsync(CancellationToken.None);
    }

    [PostgreSqlFact]
    public async Task Presenting_a_file_wakes_a_subscriber()
    {
        await using var db = await SessionFilePostgresFixture.CreateAsync();
        await using var bus = CreateBus(db.ConnectionString);
        await bus.StartAsync(CancellationToken.None);

        var sessionId = $"s-{Guid.NewGuid():N}";
        await db.Registry.InsertAsync(Ready("f1", sessionId));

        using var subscription = bus.Subscribe(sessionId);
        using var cts = new CancellationTokenSource(Delivery);
        var events = subscription.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        await db.Registry.SetPresentationAsync(sessionId, "f1", "alice");

        Assert.True(await events.MoveNextAsync());
        Assert.Equal(SessionEventKinds.Files, events.Current);
        await events.DisposeAsync();
        await bus.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// The point of routing through the database rather than an in-process bus: the publisher
    /// and the subscriber here are separate bus instances with separate connections, standing
    /// in for two backend replicas.
    /// </summary>
    [PostgreSqlFact]
    public async Task An_event_published_on_one_instance_reaches_a_subscriber_on_another()
    {
        await using var db = await SessionFilePostgresFixture.CreateAsync();
        await using var publisher = CreateBus(db.ConnectionString);
        await using var subscriber = CreateBus(db.ConnectionString);
        await publisher.StartAsync(CancellationToken.None);
        await subscriber.StartAsync(CancellationToken.None);

        var sessionId = $"s-{Guid.NewGuid():N}";
        using var subscription = subscriber.Subscribe(sessionId);
        using var cts = new CancellationTokenSource(Delivery);
        var events = subscription.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        await publisher.PublishAsync(sessionId, SessionEventKinds.Files, cts.Token);

        Assert.True(await events.MoveNextAsync());
        Assert.Equal(SessionEventKinds.Files, events.Current);
        await events.DisposeAsync();
        await publisher.StopAsync(CancellationToken.None);
        await subscriber.StopAsync(CancellationToken.None);
    }

    /// <summary>
    /// Sessions must not hear each other's events: a subscriber to one session is a workspace
    /// pane, and waking it for an unrelated write would reintroduce the refetch storm this
    /// whole mechanism exists to remove.
    /// </summary>
    [PostgreSqlFact]
    public async Task A_subscriber_hears_only_its_own_session()
    {
        await using var db = await SessionFilePostgresFixture.CreateAsync();
        await using var bus = CreateBus(db.ConnectionString);
        await bus.StartAsync(CancellationToken.None);

        var watched = $"s-{Guid.NewGuid():N}";
        var other = $"s-{Guid.NewGuid():N}";
        using var subscription = bus.Subscribe(watched);
        using var cts = new CancellationTokenSource(Delivery);
        var events = subscription.ReadAllAsync(cts.Token).GetAsyncEnumerator(cts.Token);

        await db.Registry.InsertAsync(Ready("other", other));
        await db.Registry.InsertAsync(Ready("mine", watched));

        // The write to the other session came first, so if sessions leaked into one another
        // this would surface as an extra event ahead of the one we want — and because both
        // carry the same kind, the ordering is the only thing that can catch it.
        Assert.True(await events.MoveNextAsync());
        Assert.Equal(SessionEventKinds.Files, events.Current);

        // The leak would already have been delivered by now, so a short wait is enough; the
        // channel is never completed, and reading it to exhaustion would simply hang.
        var extra = events.MoveNextAsync().AsTask();
        var winner = await Task.WhenAny(extra, Task.Delay(TimeSpan.FromMilliseconds(750)));
        Assert.False(ReferenceEquals(winner, extra),
            "a write to an unrelated session must not wake this subscriber");

        // Asserted rather than swallowed: the pending read has to end by cancellation, and a read
        // that completed instead would be the leaked event this test exists to catch.
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => extra);
        await events.DisposeAsync();
        await bus.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void Payload_round_trips_and_malformed_input_is_rejected()
    {
        Assert.True(PostgresSessionEventBus.TryDecode(
            PostgresSessionEventBus.Encode("s1", "files"), out var sessionId, out var kind));
        Assert.Equal("s1", sessionId);
        Assert.Equal("files", kind);

        Assert.False(PostgresSessionEventBus.TryDecode(null, out _, out _));
        Assert.False(PostgresSessionEventBus.TryDecode("", out _, out _));
        Assert.False(PostgresSessionEventBus.TryDecode("no-separator", out _, out _));
        Assert.False(PostgresSessionEventBus.TryDecode("\nfiles", out _, out _));
        Assert.False(PostgresSessionEventBus.TryDecode("s1\n", out _, out _));
    }

    private static PostgresSessionEventBus CreateBus(string connectionString) =>
        new(
            new ConfigurationBuilder()
                .AddInMemoryCollection(new Dictionary<string, string?>
                {
                    ["ConnectionStrings:Postgres"] = connectionString,
                })
                .Build(),
            NullLogger<PostgresSessionEventBus>.Instance);

    private static SessionFileRecord Ready(string id, string sessionId) => new(
        id, sessionId, "alice", $"{id}.txt", ".txt", "text/plain", null, 3,
        SessionFileStorageKind.S3, $"files/{id}", SessionFileState.Ready,
        SessionFilePreviewState.None, null, "alice", "upload",
        DateTime.UtcNow, DateTime.UtcNow, null);
}
