using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;

namespace AgentHub.Api.Services;

/// <summary>
/// The models a provider account offers, as the session pod's probe of the pinned CLI reported
/// them (#461, docs/design/model-discovery.md). The backend never asks a provider itself; it keeps
/// the last answer per owner and account so the new-session dialog has a list before a pod runs.
/// </summary>
public sealed record ModelCatalog
{
    public string? Agent { get; init; }
    /// <summary><c>cli</c> (the CLI's own list), <c>endpoint</c> (a custom endpoint's /models) or <c>none</c>.</summary>
    public string Source { get; init; } = "cli";
    /// <summary>True when the list came from the provider for this account, not from the CLI version alone.</summary>
    public bool Verified { get; init; }
    /// <summary><c>ok</c>, <c>fallback</c> (the CLI's built-in list after a refusal), <c>error</c> or <c>unsupported</c>.</summary>
    public string State { get; init; } = "error";
    public string? CliVersion { get; init; }
    public IReadOnlyList<CatalogModel> Models { get; init; } = [];
    public CatalogError? Error { get; init; }
}

public sealed record CatalogModel
{
    public required string Id { get; init; }
    public string? Label { get; init; }
    public string? Description { get; init; }
    public string? ResolvedId { get; init; }
    public bool? IsDefault { get; init; }
    public CatalogCapabilities? Capabilities { get; init; }
}

public sealed record CatalogCapabilities
{
    public IReadOnlyList<string>? ReasoningEfforts { get; init; }
    public string? DefaultEffort { get; init; }
    public long? ContextWindow { get; init; }
    public bool? ImageInput { get; init; }
    public bool? Reasoning { get; init; }
    public bool? ToolCall { get; init; }
    public bool? FastMode { get; init; }
    public bool? AdaptiveThinking { get; init; }
}

public sealed record CatalogError(string Code, string Message);

/// <summary>The answer of the catalog routes: the stored catalog and how old it is.</summary>
public sealed record ModelCatalogView
{
    /// <summary><c>fresh</c> (under a day), <c>stale</c> (older) or <c>unknown</c> (never probed for this account).</summary>
    public required string Freshness { get; init; }
    public DateTimeOffset? FetchedAt { get; init; }
    public ModelCatalog? Catalog { get; init; }
    /// <summary>The session's chosen model, and whether the catalog lists it (session route only).</summary>
    public string? Selected { get; init; }
    public bool? SelectedListed { get; init; }
    /// <summary>How a pick from the Workspace applies (session route only): <c>live</c>, <c>restart</c>
    /// or <c>unsupported</c> (ModelSwitchPolicy), and whether this reader may switch at all.</summary>
    public string? SwitchMode { get; init; }
    public bool? CanSwitch { get; init; }
}

/// <summary>Who a catalog belongs to: one owner's account (or key, or endpoint) for one agent.</summary>
public sealed record ModelCatalogKey(string Owner, AgentKind Agent, AgentAuthMode AuthMode, string Account, string Endpoint)
{
    /// <summary>
    /// The account a session's pod actually used: the login the last start mounted (which an
    /// account switch also records), the owner's API key, or the endpoint. A session following the
    /// default account is filed under the account that was the default when it started.
    /// </summary>
    public static ModelCatalogKey ForSession(SessionRecord session) => For(session.Owner, session.Agent, session.AuthMode,
        session.ResolvedCredentialId, session.ModelEndpointUrl);

    public static ModelCatalogKey For(string owner, AgentKind agent, AgentAuthMode authMode, string? credentialId, string? endpointUrl)
    {
        var account = authMode switch
        {
            AgentAuthMode.ApiKey => "apikey",
            AgentAuthMode.CustomEndpoint => "endpoint",
            // Auto without a stored login runs on the API key.
            AgentAuthMode.Auto => string.IsNullOrEmpty(credentialId) ? "apikey" : "login:" + credentialId,
            _ => string.IsNullOrEmpty(credentialId) ? "login:" : "login:" + credentialId
        };
        // Auto and Subscription on the same login get the same list from the CLI, and so do Auto
        // and ApiKey on the key; filing them together lets either dialog choice find it.
        var mode = authMode == AgentAuthMode.Auto
            ? (account == "apikey" ? AgentAuthMode.ApiKey : AgentAuthMode.Subscription)
            : authMode;
        var endpoint = authMode == AgentAuthMode.CustomEndpoint ? (endpointUrl ?? "").Trim().TrimEnd('/') : "";
        return new ModelCatalogKey(owner, agent, mode, account, endpoint);
    }
}

public static partial class ModelCatalogSanitizer
{
    public const int MaxModels = 200;
    public const int MaxBytes = 512 * 1024;
    public static readonly TimeSpan FreshFor = TimeSpan.FromHours(24);

    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull
    };

    // The same ids the runtime admits (common/model-catalog.js): no whitespace, quotes or anything
    // a shell, a TOML string or Cursor's option parser would read differently.
    [GeneratedRegex(@"^[A-Za-z0-9][A-Za-z0-9._:/@\[\]+=,-]{0,199}$")]
    private static partial Regex ModelId();
    [GeneratedRegex("^[a-z][a-z0-9_-]{0,23}$")]
    private static partial Regex Effort();
    [GeneratedRegex("^[a-z_]{1,40}$")]
    private static partial Regex ErrorCode();

    private static readonly HashSet<string> States = ["ok", "fallback", "error", "unsupported"];
    private static readonly HashSet<string> Sources = ["cli", "endpoint", "none"];

    /// <summary>
    /// The pod's answer rebuilt from known fields with the runtime's bounds, or null when it is
    /// not a catalog. The pod runs code the owner's agent controls, and viewers read the result.
    /// </summary>
    public static ModelCatalog? Parse(string? json)
    {
        if (string.IsNullOrEmpty(json) || json.Length > MaxBytes) return null;
        ModelCatalog? raw;
        try { raw = JsonSerializer.Deserialize<ModelCatalog>(json, Json); }
        catch (JsonException) { return null; }
        return raw is null ? null : Clean(raw);
    }

    public static ModelCatalog Clean(ModelCatalog raw)
    {
        var models = new List<CatalogModel>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var model in (raw.Models ?? []).Where(model => model?.Id is not null && ModelId().IsMatch(model.Id) && seen.Add(model.Id)))
        {
            models.Add(new CatalogModel
            {
                Id = model.Id,
                Label = Clip(model.Label, 120),
                Description = Clip(model.Description, 300),
                ResolvedId = model.ResolvedId is { } resolved && ModelId().IsMatch(resolved) ? resolved : null,
                IsDefault = model.IsDefault == true ? true : null,
                Capabilities = Clean(model.Capabilities)
            });
            if (models.Count >= MaxModels) break;
        }
        return new ModelCatalog
        {
            Agent = Clip(raw.Agent, 40),
            Source = raw.Source is { } source && Sources.Contains(source) ? source : "cli",
            Verified = raw.Verified,
            State = raw.State is { } state && States.Contains(state) ? state : "error",
            CliVersion = Clip(raw.CliVersion, 60),
            Models = models,
            Error = raw.Error is { } error
                ? new CatalogError(error.Code is { } code && ErrorCode().IsMatch(code) ? code : "probe_failed", Clip(error.Message, 300) ?? "")
                : null
        };
    }

    private static CatalogCapabilities? Clean(CatalogCapabilities? caps)
    {
        if (caps is null) return null;
        var efforts = caps.ReasoningEfforts?.Where(e => e is not null && Effort().IsMatch(e)).Distinct().Take(12).ToList();
        var clean = new CatalogCapabilities
        {
            ReasoningEfforts = efforts is { Count: > 0 } ? efforts : null,
            DefaultEffort = caps.DefaultEffort is { } effort && Effort().IsMatch(effort) ? effort : null,
            ContextWindow = caps.ContextWindow is > 0 and < 1_000_000_000_000 ? caps.ContextWindow : null,
            ImageInput = caps.ImageInput, Reasoning = caps.Reasoning, ToolCall = caps.ToolCall,
            FastMode = caps.FastMode, AdaptiveThinking = caps.AdaptiveThinking
        };
        return clean == new CatalogCapabilities() ? null : clean;
    }

    private static string? Clip(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var clean = new string(text.Where(c => !char.IsControl(c)).ToArray()).Trim();
        if (clean.Length == 0) return null;
        return clean.Length > max ? clean[..(max - 1)] + "…" : clean;
    }

    public static ModelCatalogView View(StoredModelCatalog? stored, DateTimeOffset now, string? selected = null)
    {
        if (stored is null)
            return new ModelCatalogView { Freshness = "unknown", Selected = selected };
        return new ModelCatalogView
        {
            Freshness = now - stored.FetchedAt < FreshFor ? "fresh" : "stale",
            FetchedAt = stored.FetchedAt,
            Catalog = stored.Catalog,
            Selected = selected,
            SelectedListed = selected is null || stored.Catalog.State is "error" or "unsupported"
                ? null
                : stored.Catalog.Models.Any(model => model.Id == selected || model.ResolvedId == selected)
        };
    }
}
