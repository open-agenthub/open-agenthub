using AgentHub.Api.Notifications;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Chat.Signal;

/// <summary>
/// Notifier that mirrors a session into the owner's Signal chat: on every "question"
/// event it sends the agent's question (split across messages when long), on
/// finished/failed a closing line. Replies are handled by <see cref="SignalReceiveService"/>.
/// Signal has no topics and no message edits — everything is a plain message in the
/// user's own conversation, addressed by their verified number. Community feature —
/// no license required. SECURITY: phone numbers are PII — logs carry owner/session
/// ids only, never the number.
/// </summary>
public sealed class SignalNotifier : INotifier
{
    private readonly SignalOptions _opts;
    private readonly SignalClient _signal;
    private readonly ChatBindingStore _bindings;
    private readonly UserDirectory _users;
    private readonly WorkingIndicator _indicator;
    private readonly string _frontendOrigin;
    private readonly ILogger<SignalNotifier> _log;

    public SignalNotifier(SignalOptions opts, SignalClient signal, ChatBindingStore bindings,
        UserDirectory users, WorkingIndicator indicator, IConfiguration cfg, ILogger<SignalNotifier> log)
    {
        _opts = opts; _signal = signal; _bindings = bindings; _users = users; _indicator = indicator;
        _frontendOrigin = (cfg["FrontendOrigin"] ?? "").TrimEnd('/');
        _log = log;
    }

    public async Task NotifyAsync(SessionRecord s, string eventType, string message, CancellationToken ct = default)
    {
        if (!_opts.CanRun) return;
        try
        {
            var binding = await _bindings.GetAsync("signal", s.Id, ct);

            // The session produced an event — the working indicator (if any) is obsolete.
            // On Signal it is a static message (no edits), so remove it best-effort.
            _indicator.Stop(s.Id);

            // Numbers get recycled and users opt out or verify a new number: the stored
            // binding is only a hint — the user row decides where (and whether) to send.
            if (binding is not null && !await IsBindingCurrentAsync(binding, ct))
            {
                if (eventType != "question") return; // never send to a stale/opted-out target
                binding = null; // the create path re-validates and re-binds at the current number
            }

            if (binding?.StatusRef is { } statusRef)
            {
                await _signal.TryDeleteAsync(binding.ChatId, statusRef, ct);
                await _bindings.SetStatusRefAsync("signal", s.Id, null, ct);
            }

            if (eventType is "finished" or "failed")
            {
                if (binding is not null)
                {
                    var ts = await _signal.SendAsync(binding.ChatId, $"🏁 {eventType} — {message}", ct);
                    if (ts is not null)
                        await _bindings.RecordMessageAsync("signal", binding.ChatId, ts, s.Id, ct);
                }
                return;
            }
            if (eventType != "question") return;

            if (binding is null)
            {
                binding = await CreateBindingAsync(s, ct);
                if (binding is null) return; // user not linked/verified or opted out
            }

            // The most recent question owns the chat's plain replies (reply-routing mode).
            await _bindings.SetActiveAsync("signal", binding.ChatId, s.Id, ct);

            var messages = BuildAnswerMessages(message);
            for (var i = 0; i < messages.Count; i++)
            {
                if (i > 0) await Task.Delay(1100, ct); // pace multi-message answers
                var ts = await _signal.SendAsync(binding.ChatId, messages[i], ct);
                if (ts is null)
                {
                    _log.LogWarning("Signal chunk {Index}/{Count} failed for session {Id} — stopping to avoid silent gaps", i + 1, messages.Count, s.Id);
                    break;
                }
                await _bindings.RecordMessageAsync("signal", binding.ChatId, ts, s.Id, ct);
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Signal notify failed for session {Id}", s.Id); }
    }

    /// <summary>
    /// First contact: sends the session header (with the session link and reply
    /// instructions) to the owner's verified number and creates the binding. Null when
    /// the owner has no verified/enabled Signal number or the header could not be sent.
    /// </summary>
    private async Task<ChatBinding?> CreateBindingAsync(SessionRecord s, CancellationToken ct)
    {
        var user = await _users.GetAsync(s.Owner, ct);
        if (user is not { SignalEnabled: true, SignalVerified: true, SignalNumber: not null }) return null;

        var header = ChatFormatting.Header(s.Id, s.Title) + $" ({s.Mode})\n" +
                     (string.IsNullOrEmpty(_frontendOrigin) ? "" : $"{_frontendOrigin}/s/{s.Id}\n") +
                     "Quote a message of this session to answer it; plain replies go to the newest session. !status shows progress.";

        var headerTs = await _signal.SendAsync(user.SignalNumber, header, ct);
        if (headerTs is null) return null;

        // Active=false per the store contract — SetActiveAsync flips it right after.
        var binding = new ChatBinding("signal", s.Id, s.Owner, user.SignalNumber, null, null, false);
        await _bindings.UpsertAsync(binding, ct);
        await _bindings.RecordMessageAsync("signal", binding.ChatId, headerTs, s.Id, ct);
        return binding;
    }

    /// <summary>True when the binding's target still is the owner's current, verified and
    /// enabled Signal number — a stale target must never receive session content.</summary>
    private async Task<bool> IsBindingCurrentAsync(ChatBinding binding, CancellationToken ct)
    {
        var user = await _users.GetAsync(binding.Owner, ct);
        return user is { SignalEnabled: true, SignalVerified: true } && user.SignalNumber == binding.ChatId;
    }

    /// <summary>
    /// Builds the labeled Signal messages for one agent answer (pure — exposed for tests).
    /// Thin wrapper over the shared <see cref="ChatFormatting.BuildAnswerMessages"/>.
    /// </summary>
    public static IReadOnlyList<string> BuildAnswerMessages(string message)
        => ChatFormatting.BuildAnswerMessages(message);
}
