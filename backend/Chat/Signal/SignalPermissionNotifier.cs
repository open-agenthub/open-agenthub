using AgentHub.Api.Permissions;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Chat.Signal;

/// <summary>
/// Posts a tool-permission request to the owner's Signal chat. Signal has no buttons,
/// so the decision is reaction-based: 👍 allows, 👎 denies, and quoting the prompt with
/// "always" grants allowAlways — all handled by <see cref="SignalReceiveService"/>.
/// Returns false when the user has no verified Signal target (or the integration is
/// off), so the caller falls back to the next notifier / the normal prompt. Community
/// feature — no license required.
/// </summary>
public sealed class SignalPermissionNotifier : IPermissionNotifier, IPermissionPromptEditor
{
    public string Platform => "signal";

    private readonly SignalOptions _opts;
    private readonly SignalClient _signal;
    private readonly ChatBindingStore _bindings;
    private readonly UserDirectory _users;
    private readonly PermissionStore _store;

    public SignalPermissionNotifier(SignalOptions opts, SignalClient signal,
        ChatBindingStore bindings, UserDirectory users, PermissionStore store)
    { _opts = opts; _signal = signal; _bindings = bindings; _users = users; _store = store; }

    public async Task<bool> PostAsync(PermissionRequest req, CancellationToken ct = default)
    {
        if (!_opts.CanRun) return false;

        // Prefer the session's existing conversation, else the owner's verified number.
        // A binding whose target no longer matches the owner's current verified, enabled
        // number (numbers get recycled, users opt out) is treated as absent.
        string number;
        var binding = await _bindings.GetAsync("signal", req.SessionId, ct);
        if (binding is not null && await IsBindingCurrentAsync(binding, ct))
        {
            number = binding.ChatId;
        }
        else
        {
            var user = await _users.GetAsync(req.Owner, ct);
            if (user is not { SignalEnabled: true, SignalVerified: true, SignalNumber: not null }) return false;
            number = user.SignalNumber;
        }

        // Plain text — the summary goes out verbatim, just capped.
        var text = $"🔒 The agent wants to use {req.Tool}.";
        if (!string.IsNullOrWhiteSpace(req.Summary)) text += "\n" + Trim(req.Summary!, 600);
        text += "\nReact 👍 to allow, 👎 to deny — or quote this message and reply \"always\".";

        var ts = await _signal.SendAsync(number, text, ct);
        if (ts is null) return false;
        await _store.SetPromptMessageAsync(req.Id, "signal", number, ts, ct);
        return true;
    }

    /// <summary>True when the binding's target still is the owner's current, verified and
    /// enabled Signal number — a stale target must never receive a permission prompt.</summary>
    private async Task<bool> IsBindingCurrentAsync(ChatBinding binding, CancellationToken ct)
    {
        var user = await _users.GetAsync(binding.Owner, ct);
        return user is { SignalEnabled: true, SignalVerified: true } && user.SignalNumber == binding.ChatId;
    }

    /// <summary>The request can no longer be answered. Signal cannot edit the prompt,
    /// so a short follow-up message says so instead.</summary>
    public async Task MarkExpiredAsync(PermissionRequest req, CancellationToken ct = default)
    {
        if (req.Channel is null) return;
        await _signal.SendAsync(req.Channel, ExpiredText(req.Tool), ct);
    }

    /// <summary>Follow-up text once the request can no longer be answered out-of-band.</summary>
    public static string ExpiredText(string tool)
        => $"⏰ The permission request for {tool} expired — please answer in the web terminal.";

    private static string Trim(string s, int max) => s.Length <= max ? s : s[..max] + " …";
}
