using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using AgentHub.Api.Services;
using AgentHub.Api.Webhooks;

namespace AgentHub.Api.Tests;

internal sealed class InMemoryWebhookTriggerStore : IWebhookTriggerStore
{
    public List<WebhookTriggerRecord> Records { get; } = new();
    public int TouchCalls { get; private set; }
    public string? LastTouchedId { get; private set; }

    public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;

    public Task<IReadOnlyList<WebhookTriggerInfo>> ListAsync(string owner, CancellationToken ct = default)
        => Task.FromResult<IReadOnlyList<WebhookTriggerInfo>>(
            Records.Where(r => r.Owner == owner).Select(r => r.Info).ToList());

    public Task<WebhookTriggerRecord?> GetByIdAsync(string id, CancellationToken ct = default)
        => Task.FromResult(Records.FirstOrDefault(r => r.Info.Id == id));

    public Task<WebhookTriggerInfo> CreateAsync(string owner, CreateWebhookTriggerRequest request,
        string secretProtected, CancellationToken ct = default)
    {
        var info = new WebhookTriggerInfo
        {
            Id = Guid.NewGuid().ToString("n"),
            Name = request.Name,
            ProviderId = request.ProviderId,
            Events = request.Events,
            RepoFilter = request.RepoFilter,
            PromptTemplate = request.PromptTemplate,
            ProjectId = request.ProjectId,
            Agent = request.Agent,
            AutoApprove = request.AutoApprove,
            CreatedAt = DateTime.UtcNow
        };
        Records.Add(new WebhookTriggerRecord { Owner = owner, SecretProtected = secretProtected, Info = info });
        return Task.FromResult(info);
    }

    public Task<bool> DeleteAsync(string owner, string id, CancellationToken ct = default)
        => Task.FromResult(Records.RemoveAll(r => r.Owner == owner && r.Info.Id == id) > 0);

    public Task TouchAsync(string id, CancellationToken ct = default)
    {
        TouchCalls++;
        LastTouchedId = id;
        return Task.CompletedTask;
    }
}

/// <summary>Reversible marker "protection" so tests can assert plaintext never hits the store.</summary>
internal sealed class PlainWebhookSecretProtector : IWebhookSecretProtector
{
    public string Protect(string plaintext) => "protected:" + plaintext;
    public string Unprotect(string protectedPayload)
        => protectedPayload.StartsWith("protected:", StringComparison.Ordinal)
            ? protectedPayload["protected:".Length..]
            : throw new InvalidOperationException("Payload was not protected.");
}

internal class RecordingWebhookSessionService : ISessionService
{
    public List<(string Owner, CreateSessionRequest Request)> Created { get; } = new();
    public Exception? CreateException { get; init; }

    public Task<SessionInfo> CreateSessionAsync(string owner, CreateSessionRequest req, CancellationToken ct = default)
    {
        if (CreateException is not null) throw CreateException;
        Created.Add((owner, req));
        return Task.FromResult(new SessionInfo
        {
            Id = $"session-{Created.Count}", Title = req.Title, Owner = owner,
            Mode = req.Mode, Phase = "Pending"
        });
    }

    public Task StoreCredentialsAsync(string owner, UserCredentials creds, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<CredentialStatus> GetCredentialStatusAsync(string owner, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task DeleteProviderCredentialsAsync(string owner, AgentKind agent, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task StoreProviderCredentialsAsync(string owner, AgentKind agent, string json, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<SessionInfo> DuplicateSessionAsync(string owner, string id, DuplicateSessionRequest request, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<SessionInfo> ResumeSessionAsync(string owner, string id, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<SessionInfo> PauseSessionAsync(string owner, string id, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<SessionInfo> UpdateSessionAsync(string owner, string id, UpdateSessionRequest req, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task<IReadOnlyList<SessionInfo>> ListSessionsAsync(string owner, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public virtual Task<SessionInfo?> GetSessionAsync(string owner, string id, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task ClearQuestionAsync(string owner, string id, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public virtual Task<string?> GetTranscriptAsync(string owner, string id, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public virtual Task<string?> GetScrollbackAsync(string owner, string id, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public virtual Task<IReadOnlyList<TranscriptEntry>?> GetConversationAsync(string owner, string id, CancellationToken ct = default) =>
        Task.FromResult<IReadOnlyList<TranscriptEntry>?>(null);
    public Task<string?> MintArtifactUploadUrlAsync(string sessionId, string token, string name, CancellationToken ct = default) =>
        throw new NotSupportedException();
    public Task DeleteSessionAsync(string owner, string id, CancellationToken ct = default) =>
        throw new NotSupportedException();
}
