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
    private readonly AgentHub.Api.Chat.WorkingIndicator _indicator;
    private readonly string _frontendOrigin;
    private readonly ILogger<SlackNotifier> _log;

    public SlackNotifier(SlackOptions opts, IEnterpriseLicense license, SlackClient slack,
        SlackThreadStore threads, ISlackTargetResolver resolver, AgentHub.Api.Chat.WorkingIndicator indicator,
        IConfiguration cfg, ILogger<SlackNotifier> log)
    {
        _opts = opts; _license = license; _slack = slack; _threads = threads; _resolver = resolver;
        _indicator = indicator;
        _frontendOrigin = (cfg["FrontendOrigin"] ?? "").TrimEnd('/');
        _log = log;
    }

    public async Task NotifyAsync(SessionRecord s, string eventType, string message, CancellationToken ct = default)
    {
        if (!_opts.CanPost || !_license.Enabled) return;

        try
        {
            var thread = await _threads.GetBySessionAsync(s.Id, ct);

            // The session progressed — stop the "working…" animation and remove the
            // status message (cross-replica via the persisted ts).
            _indicator.Stop(s.Id);
            if (thread?.StatusTs is { } statusTs)
            {
                await _slack.DeleteMessageAsync(thread.Channel, statusTs, ct);
                await _threads.SetStatusTsAsync(s.Id, null, ct);
            }

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
                var header = $":robot_face: *Open AgentHub* · *{Escape(s.Title)}* (`{s.Mode}`)\n" +
                             $"Session `{s.Id}` — owner `{s.Owner}`\n" +
                             (string.IsNullOrEmpty(_frontendOrigin) ? "" : $"<{_frontendOrigin}/s/{s.Id}|Open the session ↗>\n") +
                             "_Your coding agent needs you. Reply in this thread to answer it._";
                var ts = await _slack.PostMessageAsync(channel, header, null, ct);
                if (ts is null) return;
                thread = new SlackThread(s.Id, s.Owner, channel, ts, 0);
                await _threads.UpsertAsync(thread, ct);
            }

            // Post the question itself as a clean Slack quote, split across follow-up
            // messages if it exceeds Slack's comfortable size. We deliberately do NOT
            // dump the terminal scrollback: Claude's full-screen TUI is a mess of ANSI
            // redraws once stripped. The web terminal (linked in the thread header) has
            // the full context; here we keep it readable and answerable.
            var messages = BuildAnswerMessages(message);
            for (var i = 0; i < messages.Count; i++)
            {
                if (i > 0) await Task.Delay(1100, ct); // Slack tolerates ~1 msg/s/channel
                if (await _slack.PostMessageAsync(thread.Channel, messages[i], thread.ThreadTs, ct) is null)
                {
                    _log.LogWarning("Slack chunk {Index}/{Count} failed for session {Id} — stopping to avoid silent gaps", i + 1, messages.Count, s.Id);
                    break;
                }
            }
        }
        catch (Exception ex) { _log.LogWarning(ex, "Slack notify failed for session {Id}", s.Id); }
    }

    /// <summary>
    /// Builds the labeled, blockquoted Slack messages for one agent answer (pure — exposed
    /// for tests). Splits the raw text first and escapes each chunk afterwards, so a hard
    /// split can never cut through an escaped entity and escaping never inflates a chunk
    /// past the split point. Each line must START with "&gt; " for Slack to render it as
    /// a blockquote.
    /// </summary>
    public static IReadOnlyList<string> BuildAnswerMessages(string message)
    {
        var chunks = AgentHub.Api.Chat.ChatFormatting.Split(message.Trim(), 3800);
        return chunks.Select((c, i) =>
        {
            var quoted = string.Join("\n", Escape(c).Split('\n').Select(l => "> " + l));
            var label = i == 0 ? ":speech_balloon: *The agent says:*" : $"_… ({i + 1}/{chunks.Count})_";
            return label + "\n" + quoted;
        }).ToList();
    }

    private static string Escape(string s) => s.Replace("&", "&amp;").Replace("<", "&lt;").Replace(">", "&gt;");
}
