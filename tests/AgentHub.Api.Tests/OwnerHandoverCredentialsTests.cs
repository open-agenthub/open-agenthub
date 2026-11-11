using System.Text;
using AgentHub.Api.Admin;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>The merge rules for stored credentials in an owner handover (docs/apikey-handover.md).</summary>
public class OwnerHandoverCredentialsTests
{
    private static byte[] B(string s) => Encoding.UTF8.GetBytes(s);
    private static string S(byte[] b) => Encoding.UTF8.GetString(b);

    [Fact]
    public void The_targets_own_single_slots_win_and_are_reported_without_values()
    {
        var target = new Dictionary<string, byte[]> { ["ssh_key"] = B("target-key"), ["git_user_name"] = B("Alice") };
        var source = new Dictionary<string, byte[]>
        {
            ["ssh_key"] = B("source-key"), ["anthropic_api_key"] = B("sk-source"), ["git_user_name"] = B("Admin"),
        };
        var merge = OwnerHandoverCredentials.MergeGeneral(target, source);

        Assert.Equal("target-key", S(merge.Data["ssh_key"]));
        Assert.Equal("Alice", S(merge.Data["git_user_name"]));
        Assert.Equal("sk-source", S(merge.Data["anthropic_api_key"]));
        Assert.Contains(new CredentialKindPreview(OwnerHandoverCredentials.SshKey, 1, 1), merge.Kinds);
        Assert.Contains(new CredentialKindPreview(OwnerHandoverCredentials.ApiKeys, 1, 0), merge.Kinds);
        Assert.Contains(new CredentialKindPreview(OwnerHandoverCredentials.GitIdentity, 1, 1), merge.Kinds);
        Assert.DoesNotContain(merge.Kinds, k => k.Kind.Contains("sk-") || k.Kind.Contains("key-"));
    }

    [Fact]
    public void Pats_merge_by_id_and_the_targets_host_default_stays()
    {
        var target = new Dictionary<string, byte[]>();
        GitPatStore.Write(target, [new GitPatStore.Entry("t1", GitPatKind.GitHub, "github.com", "ghp_target", Default: true)]);
        var source = new Dictionary<string, byte[]>();
        GitPatStore.Write(source, [
            new GitPatStore.Entry("s1", GitPatKind.GitHub, "github.com", "ghp_source", Default: true),
            new GitPatStore.Entry("s2", GitPatKind.GitLab, "gitlab.example.com", "glpat-source", Default: true),
        ]);

        var merge = OwnerHandoverCredentials.MergeGeneral(target, source);
        var pats = GitPatStore.Read(merge.Data);
        Assert.Equal(["t1", "s1", "s2"], pats.Select(p => p.Id));
        Assert.Equal(["t1", "s2"], GitPatStore.Defaults(pats).Select(p => p.Id));

        // A rerun on the merged data adds nothing.
        var again = OwnerHandoverCredentials.MergeGeneral(merge.Data, source);
        Assert.Equal(3, GitPatStore.Read(again.Data).Count);
    }

    [Fact]
    public void Provider_accounts_merge_by_id_without_taking_the_default()
    {
        var target = CredentialSecretFactory.CreateProviderSecret("t", "ns", "u", AgentKind.Claude, ClaudeLogin("target")).Data;
        var source = CredentialSecretFactory.CreateProviderSecret("s", "ns", "u", AgentKind.Claude, ClaudeLogin("source")).Data;
        // Both secrets were created fresh, so both have an account; give the source's a distinct id.
        var sourceSet = ProviderAccountSecret.Read(source, AgentKind.Claude);
        var renamed = new ProviderAccountSet();
        var id = ProviderAccountSecret.NewId();
        renamed.Accounts.Add(new ProviderAccount { Id = id, Label = "Admin's", IsDefault = true });
        renamed.Files[id] = sourceSet.Files[sourceSet.Accounts[0].Id];
        var sourceData = ProviderAccountSecret.Write(renamed, AgentKind.Claude);

        var merge = OwnerHandoverCredentials.MergeProvider(target, sourceData, AgentKind.Claude);
        var set = ProviderAccountSecret.Read(merge.Data, AgentKind.Claude);
        Assert.Equal(2, set.Accounts.Count);
        Assert.Equal(ProviderAccountSecret.Read(target, AgentKind.Claude).Default!.Id, set.Default!.Id);
        Assert.Equal([new CredentialKindPreview(OwnerHandoverCredentials.ProviderLogins(AgentKind.Claude), 1, 0)], merge.Kinds);
    }

    [Fact]
    public void Provider_accounts_go_to_a_target_without_any_and_keep_their_default()
    {
        var source = CredentialSecretFactory.CreateProviderSecret("s", "ns", "u", AgentKind.Codex, CodexLogin()).Data;
        var merge = OwnerHandoverCredentials.MergeProvider(null, source, AgentKind.Codex);
        var set = ProviderAccountSecret.Read(merge.Data, AgentKind.Codex);
        Assert.Single(set.Accounts);
        Assert.True(set.Accounts[0].IsDefault);
    }

    [Fact]
    public void User_secrets_keep_their_ids_and_stop_at_the_limit()
    {
        var target = new Dictionary<string, byte[]>();
        for (var i = 0; i < SessionSecretStore.MaxEntries - 1; i++)
            SessionSecretStore.Upsert(target, new UpsertSessionSecretRequest($"T{i}", "env", "v", null), DateTime.UtcNow);
        var source = new Dictionary<string, byte[]>();
        var a = SessionSecretStore.Upsert(source, new UpsertSessionSecretRequest("A", "env", "va", null), DateTime.UtcNow);
        SessionSecretStore.Upsert(source, new UpsertSessionSecretRequest("B", "env", "vb", null), DateTime.UtcNow);

        var merge = OwnerHandoverCredentials.MergeUserSecrets(target, source);
        var entries = SessionSecretStore.Read(merge.Data);
        Assert.Equal(SessionSecretStore.MaxEntries, entries.Count);
        Assert.Contains(entries, e => e.Id == a.Id && e.Name == "A");
        Assert.Equal("va", S(SessionSecretStore.Value(merge.Data, a.Id)!));
        Assert.Equal([new CredentialKindPreview(OwnerHandoverCredentials.UserSecrets, 2, 1)], merge.Kinds);
    }

    [Fact]
    public void A_connected_git_provider_moves_whole_or_not_at_all()
    {
        var target = new Dictionary<string, byte[]> { ["gitlab.access"] = B("t-a"), ["gitlab.refresh"] = B("t-r") };
        var source = new Dictionary<string, byte[]>
        {
            ["gitlab.access"] = B("s-a"), ["gitlab.refresh"] = B("s-r"),
            ["github.access"] = B("s-ga"), ["github.refresh"] = B("s-gr"),
        };
        var merge = OwnerHandoverCredentials.MergeGitOAuth(target, source);
        Assert.Equal("t-a", S(merge.Data["gitlab.access"]));
        Assert.Equal("t-r", S(merge.Data["gitlab.refresh"]));
        Assert.Equal("s-ga", S(merge.Data["github.access"]));
        Assert.Equal([new CredentialKindPreview(OwnerHandoverCredentials.GitOAuth, 2, 1)], merge.Kinds);

        var again = OwnerHandoverCredentials.MergeGitOAuth(merge.Data, new Dictionary<string, byte[]>
            { ["github.access"] = B("s-ga"), ["github.refresh"] = B("s-gr") });
        Assert.Equal([new CredentialKindPreview(OwnerHandoverCredentials.GitOAuth, 1, 0)], again.Kinds);
    }

    private static string ClaudeLogin(string who) =>
        "{\"claudeAiOauth\":{\"accessToken\":\"sk-ant-oat01-" + who + "\",\"refreshToken\":\"sk-ant-ort01-" + who
        + "\",\"expiresAt\":4102444800000,\"scopes\":[\"user:inference\"]}}";

    private static string CodexLogin() =>
        """{"OPENAI_API_KEY":null,"tokens":{"id_token":"x.y.z","access_token":"a","refresh_token":"r","account_id":"acc"},"last_refresh":"2026-11-01T00:00:00Z"}""";
}
