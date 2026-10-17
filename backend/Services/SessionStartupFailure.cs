using System.Text.RegularExpressions;
using System.Text.Json;
using AgentHub.Api.Models;
using k8s.Models;

namespace AgentHub.Api.Services;

public sealed record SessionStartupFailure(string Step, string Message, bool Terminal)
{
    public static SessionStartupFailure? FromPod(V1Pod? pod, IReadOnlyList<RepoRef> repos)
    {
        var status = pod?.Status;
        if (status is null) return null;
        foreach (var container in (status.InitContainerStatuses ?? []).Concat(status.ContainerStatuses ?? []))
        {
            var init = status.InitContainerStatuses?.Contains(container) == true;
            if (container.State?.Terminated is { ExitCode: not 0 } dead)
            {
                var step = container.Name == "git-clone" ? "clone" : init ? "initialization" : "agent start";
                var detail = dead.Message ?? dead.Reason ?? $"Exit code {dead.ExitCode}";
                if (step == "clone") detail = CloneDetail(detail, repos);
                if (dead.Reason == "OOMKilled") detail = "The container ran out of memory. Increase the session's memory limit.";
                return Failure(step, $"{step}: {detail}", true);
            }
            if (container.State?.Waiting is { } waiting && waiting.Reason is not (null or "ContainerCreating" or "PodInitializing"))
            {
                var image = waiting.Reason is "ImagePullBackOff" or "ErrImagePull" or "InvalidImageName";
                var step = image ? "image pull" : init ? "initialization" : "agent start";
                return Failure(step, $"{step}: {waiting.Message ?? waiting.Reason}" +
                    (image ? " Check the image name and registry access." : ""), false);
            }
        }
        var unscheduled = status.Conditions?.FirstOrDefault(c => c.Type == "PodScheduled" && c.Status == "False");
        if (unscheduled is not null)
            return Failure("scheduling", $"Scheduling: {unscheduled.Message ?? unscheduled.Reason}. Check available cluster capacity and scheduling constraints.", false);
        if (status.Phase == "Failed")
            return Failure("agent start", $"Session failed: {status.Message ?? status.Reason ?? "The pod failed without a diagnostic."}", true);
        return null;
    }

    public static SessionStartupFailure Admission(string message)
    {
        // API admission refusals arrive as a Status JSON document, not a human message.
        // Exposing the whole response obscures the remedy and copies irrelevant fields.
        try
        {
            using var status = JsonDocument.Parse(message);
            if (status.RootElement.TryGetProperty("message", out var detail) && detail.ValueKind == JsonValueKind.String)
                message = detail.GetString()!;
        }
        catch (JsonException) { }
        var quota = message.Contains("quota", StringComparison.OrdinalIgnoreCase);
        return Failure(quota ? "quota" : "scheduling", quota
            ? $"Quota: {message}. Free resources or ask an administrator to increase the quota."
            : $"Scheduling: {message}", true);
    }

    private static SessionStartupFailure Failure(string step, string message, bool terminal)
        => new(step, Redact(message), terminal);

    private static string CloneDetail(string detail, IReadOnlyList<RepoRef> repos)
    {
        var match = Regex.Match(detail, @"repository-index: (\d+)");
        if (!match.Success || !int.TryParse(match.Groups[1].Value, out var index) || index < 1 || index > repos.Count)
            return detail + " Edit the session's repositories and check Git credentials.";
        var repo = repos[index - 1];
        var label = $"repository {repo.Url} (branch {repo.Branch ?? "default"})";
        if (detail.Contains("Remote branch", StringComparison.OrdinalIgnoreCase) && detail.Contains("not found", StringComparison.OrdinalIgnoreCase))
        {
            var defaultBranch = Regex.Match(detail, @"(?m)^default-branch: (.+)$");
            return $"Branch {repo.Branch} does not exist in {repo.Url}. " +
                (defaultBranch.Success ? $"The default branch is {defaultBranch.Groups[1].Value.Trim()}. " : "") +
                "Edit the session's repositories.";
        }
        if (detail.Contains("Authentication failed", StringComparison.OrdinalIgnoreCase) || detail.Contains("Permission denied", StringComparison.OrdinalIgnoreCase))
            return $"Cannot authenticate to {label}. Check the session's Git credentials and repository access.";
        return $"Could not clone {label}: {detail}";
    }

    // Redact before truncating: cutting a URL or header first can hide the marker needed to
    // recognize its secret. The same boundary protects persisted and freshly read failures.
    public static string Redact(string? message)
    {
        if (string.IsNullOrWhiteSpace(message)) return "";
        // Git helpers sometimes repeat a password outside its URL. Remove the exact value
        // wherever it occurs, including percent-decoded credentials, before removing userinfo.
        foreach (Match credential in Regex.Matches(message, @"(?i)[a-z][a-z0-9+.-]*://([^\s/]+)@"))
        {
            var userinfo = credential.Groups[1].Value;
            var secret = userinfo.Contains(':') ? userinfo[(userinfo.IndexOf(':') + 1)..] : userinfo;
            if (secret.Length > 0)
            {
                message = message.Replace(secret, "[redacted]", StringComparison.Ordinal);
                var decoded = Uri.UnescapeDataString(secret);
                if (decoded.Length > 0) message = message.Replace(decoded, "[redacted]", StringComparison.Ordinal);
            }
        }
        var text = Regex.Replace(message, @"(?i)([a-z][a-z0-9+.-]*://)[^\s/]*@", "$1[redacted]@");
        text = Regex.Replace(text, @"(?im)(\b(?:authorization|proxy-authorization|cookie|set-cookie|x-[a-z-]*(?:token|key))\s*[:=]\s*)[^\r\n]+", "$1[redacted]");
        text = Regex.Replace(text, @"(?i)\b(?:Bearer|Basic)\s+[^\s,;]+", "[redacted]");
        text = Regex.Replace(text, @"(?i)((?:[a-z_]*(?:token|password|passwd|secret|api[_-]?key|credential)[a-z_]*|signature)\s*[=:]\s*)[^\s&;,]+", "$1[redacted]");
        text = Regex.Replace(text, @"\b(?:gh[pousr]_[A-Za-z0-9_]+|github_pat_[A-Za-z0-9_]+|glpat-[A-Za-z0-9_-]+|sk-[A-Za-z0-9_-]+)\b", "[redacted]");
        text = Regex.Replace(text, @"[\x00-\x08\x0b-\x1f\x7f]", "").Trim();
        return text.Length > 2000 ? text[..2000] + "…" : text;
    }
}
