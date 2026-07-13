// -----------------------------------------------------------------------------
// Open AgentHub Enterprise Edition — Slack integration.
// Part of the Enterprise Edition; NOT covered by the AGPL-3.0 license of the
// open-core. Source-available under the Open AgentHub Enterprise License
// (see ee/LICENSE); a valid subscription is required for production use.
// -----------------------------------------------------------------------------
using AgentHub.Api.Licensing;
using AgentHub.Api.Notifications;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;

namespace AgentHub.Api.Ee.Slack;

/// <summary>
/// Notifier that mirrors a session into a Slack thread: on every "question" event
/// it posts the new terminal output since the last update plus the question, so the
/// whole conversation accumulates in one thread. Replies are handled by
/// <see cref="SlackSocketModeService"/>. Requires a valid enterprise license.
/// </summary>
public sealed class SlackNotifier : INotifier
{
    private readonly SlackOptions _opts;
    private readonly IEnterpriseLicense _license;
    private readonly SlackClient _slack;
    private readonly SlackThreadStore _threads;
    private readonly ISlackTargetResolver _resolver;
    private readonly string _frontendOrigin;
    private readonly ILogger<SlackNotifier> _log;

    public SlackNotifier(SlackOptions opts, IEnterpriseLicense license, SlackClient slack,
        SlackThreadStore threads, ISlackTargetResolver resolver, IConfiguration cfg, ILogger<SlackNotifier> log)
    {
        _opts = opts; _license = license; _slack = slack; _threads = threads; _resolver = resolver;
        _frontendOrigin = cfg["FrontendOrigin"] ?? "";
        _log = log;
    }

    public async Task NotifyAsync(SessionRecord s, string eventType, string message, CancellationToken ct = default)
    {
        if (!_opts.CanPost || !_license.Enabled) return;

        try
        {
            var thread = await _threads.GetBySessionAsync(s.Id, ct);

            if (eventType is "finished" or "failed")
            {
                if (thread is not null)
                    await _slack.PostMessageAsync(thread.Channel, $":checkered_flag: *{eventType}* — {message}", thread.ThreadTs, ct);
                return;
            }
            if (eventType != "question") return;

            // Create the thread on first contact — in the session owner's resolved
            // conversation (their Slack DM, an override, or the fallback channel).
            if (thread is null)
            {
                var channel = await _resolver.ResolveAsync(s.Owner, ct);
                if (channel is null) return; // no Slack target for this user (opted out / not found)
                var header = $":robot_face: *{Escape(s.Title)}* (`{s.Mode}`)\n" +
                             $"Session `{s.Id}` — owner `{s.Owner}`\n" +
                             (string.IsNullOrEmpty(_frontendOrigin) ? "" : $"{_frontendOrigin}/s/{s.Id}\n") +
                             "_Reply in this thread to answer the agent._";
                var ts = await _slack.PostMessageAsync(channel, header, null, ct);
                if (ts is null) return;
                thread = new SlackThread(s.Id, s.Owner, channel, ts, 0);
                await _threads.UpsertAsync(thread, ct);
            }

            // Post the question itself as a clean Slack quote. We deliberately do NOT
            // dump the terminal scrollback: Claude's full-screen TUI is a mess of ANSI
            // redraws once stripped. The web terminal (linked in the thread header) has
            // the full context; here we keep it readable and answerable.
            await _slack.PostMessageAsync(thread.Channel, Quote(message), thread.ThreadTs, ct);
        }
        catch (Exception ex) { _log.LogWarning(ex, "Slack notify failed for session {Id}", s.Id); }
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
    // A label line, then the message as a Slack blockquote — each line must START with
    // "> " for Slack to render it as a quote.
    private static string Quote(string s)
    {
        s = Escape(s.Trim());
        if (s.Length > 2500) s = s[..2500] + " …";
        var quoted = string.Join("\n", s.Split('\n').Select(l => "> " + l));
        return ":speech_balloon: *The agent says:*\n" + quoted;
    }
}
