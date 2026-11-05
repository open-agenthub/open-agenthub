namespace AgentHub.Api.Services;

/// <summary>
/// The commands a person pastes to move a conversation with a transfer link. They use nothing
/// but <c>curl</c> and <c>tar</c>, which Linux, macOS and Windows 10+ all ship, because the
/// person running them has neither an API token nor our tooling installed — that was the point.
/// </summary>
public static class SessionTransferCommands
{
    /// <summary>Carries the link's secret. A header rather than the path: request lines are what
    /// ingress controllers and proxies write to their access logs, headers are not.</summary>
    public const string Header = "X-AgentHub-Transfer";

    public static string Resume(string resumeId) => $"claude --resume {resumeId}";

    public static SessionTransferLink TakeLocal(string url, string secret, DateTimeOffset expires, string directory, string resumeId, string sessionId)
    {
        return new SessionTransferLink
        {
            Url = url, Secret = secret, ExpiresAt = expires,
            // -k: a transcript already on this machine is not overwritten. It may hold turns taken
            // here, and tar refusing (and stopping the chain) is the only warning there would be.
            Command = $"mkdir -p {Sh(directory)} && curl -fsSL -H {Sh($"{Header}: {secret}")} {Sh(url)} | tar -xzkf - -C \"$HOME\" && cd {Sh(directory)} && {Resume(resumeId)}",
            PowerShellCommand = $"New-Item -ItemType Directory -Force {Ps(directory)} | Out-Null; {Temp(sessionId)}; "
                + $"curl.exe -fsSL -H {Ps($"{Header}: {secret}")} {Ps(url)} -o $t; tar -xzkf $t -C \"$HOME\"; Remove-Item $t; "
                + $"Set-Location {Ps(directory)}; {Resume(resumeId)}"
        };
    }

    public static SessionTransferLink HandBack(string url, string secret, DateTimeOffset expires, string directory, string resumeId, string sessionId)
    {
        var folder = $"{SessionLocalTransfer.Projects}/{SessionLocalTransfer.ProjectFolder(directory)}/{resumeId}";
        return new SessionTransferLink
        {
            Url = url, Secret = secret, ExpiresAt = expires,
            // The folder next to the transcript holds subagent conversations and only exists once
            // one ran; naming it unconditionally makes tar fail for every session without one.
            Command = $"(cd \"$HOME\" && tar -czf - {Sh(folder + ".jsonl")} $(test -d {Sh(folder)} && echo {Sh(folder)}) "
                + $"| curl -fsS -X PUT -H 'Content-Type: application/gzip' -H {Sh($"{Header}: {secret}")} --data-binary @- {Sh(url)})",
            PowerShellCommand = $"{Temp(sessionId)}; Push-Location $HOME; $f = @({Ps(folder + ".jsonl")}); if (Test-Path {Ps(folder)}) {{ $f += {Ps(folder)} }}; "
                + $"tar -czf $t @f; Pop-Location; "
                + $"curl.exe -fsS -X PUT -H 'Content-Type: application/gzip' -H {Ps($"{Header}: {secret}")} --data-binary \"@$t\" {Ps(url)}; Remove-Item $t"
        };
    }

    /// <summary>
    /// The archive's temporary file. GetTempPath and Join-Path rather than "$env:TEMP\…": TEMP is unset
    /// in PowerShell on macOS and Linux, and a backslash there is part of a file name, not a separator.
    /// </summary>
    private static string Temp(string sessionId) => $"$t = Join-Path ([IO.Path]::GetTempPath()) 'agenthub-{sessionId}.tgz'";

    /// <summary>POSIX single quotes: nothing inside is expanded, a quote is closed, escaped and reopened.</summary>
    private static string Sh(string value) => "'" + value.Replace("'", "'\\''") + "'";

    /// <summary>PowerShell single quotes: a quote is doubled.</summary>
    private static string Ps(string value) => "'" + value.Replace("'", "''") + "'";
}
