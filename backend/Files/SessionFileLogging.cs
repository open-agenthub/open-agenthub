using Microsoft.Extensions.Logging;

namespace AgentHub.Api.Files;

public static class SessionFileLogging
{
    /// <summary>
    /// The framework category whose "Request starting …" line prints every request's query string.
    /// For the video stream route that query is a ticket (docs/session-file-video.md).
    /// </summary>
    public const string RequestStartCategory = "Microsoft.AspNetCore.Hosting.Diagnostics";

    /// <summary>
    /// Holds the request-start log at Warning whatever the configuration says, so a stream
    /// ticket never reaches a log line.
    ///
    /// The line is written before any middleware runs, so there is no point in the pipeline at
    /// which the query could be rewritten first, and editing the message would mean wrapping
    /// every logging provider. Its Information output is off by default ("Microsoft.AspNetCore":
    /// "Warning"); this keeps an operator who raises the level for debugging from turning it back
    /// on. PostConfigure appends the rule after the configuration's own, and among rules of the
    /// same category the last one wins. Routing and endpoint logs carry the path, never the query.
    /// </summary>
    public static IServiceCollection KeepStreamTicketsOutOfLogs(this IServiceCollection services) =>
        services.PostConfigure<LoggerFilterOptions>(options => options.Rules.Add(
            new LoggerFilterRule(null, RequestStartCategory, LogLevel.Warning, null)));
}
