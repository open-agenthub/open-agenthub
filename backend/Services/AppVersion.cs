using System.Reflection;

namespace AgentHub.Api.Services;

/// <summary>
/// The version the running backend reports to the UI, and where its source lives.
/// </summary>
/// <remarks>
/// The version is baked into the image at build time (<c>-p:InformationalVersion</c> from the
/// Dockerfile's <c>APP_VERSION</c> build arg) rather than read from the Helm chart or the image
/// tag: the backend cannot see which tag it was pulled under, and a chart value would have to be
/// kept in step by hand — exactly the drift this is meant to make visible. <c>AGENTHUB_VERSION</c>
/// overrides it so an operator running a custom build can still label the deployment.
/// </remarks>
public static class AppVersion
{
    public const string RepoUrl = "https://github.com/open-agenthub/open-agenthub";

    /// <summary>The label used when no version was baked in — a local build or a plain <c>dotnet run</c>.</summary>
    public const string Dev = "dev";

    public const string OverrideVariable = "AGENTHUB_VERSION";

    /// <summary>The version of this process, resolved once per process.</summary>
    public static string Current { get; } = Resolve(
        Environment.GetEnvironmentVariable(OverrideVariable),
        typeof(AppVersion).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);

    /// <summary>
    /// Picks the version to show: the environment override when set, else the assembly's
    /// informational version without any <c>+commit</c> suffix, else <see cref="Dev"/>.
    /// </summary>
    public static string Resolve(string? overrideValue, string? informationalVersion)
    {
        var fromEnv = overrideValue?.Trim();
        if (!string.IsNullOrEmpty(fromEnv)) return fromEnv;

        var baked = informationalVersion?.Trim();
        if (string.IsNullOrEmpty(baked)) return Dev;

        // The SDK appends "+<commit>" when SourceLink finds a repository at build time. The image
        // build has no .git directory, so this rarely fires, but a developer's local build does
        // produce it, and "dev+a1b2c3d" would read as a release label in the footer.
        var plus = baked.IndexOf('+');
        if (plus >= 0) baked = baked[..plus];

        return baked.Length == 0 ? Dev : baked;
    }
}
