using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Models;
using AgentHub.Api.Persistence;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// #495: the shared view picks its transport from <c>uiMode</c>. A chat-mode session reads chat
/// frames only, and a recipient told "terminal" draws the terminal composer, whose keystrokes the
/// pod drops: the Collaborator types, and nothing reaches the agent. The share store once left
/// ui_mode out of its session columns, so every shared projection fell back to the record's
/// "terminal" default, and no test noticed. Each way a session reaches a recipient is checked here
/// against the real store, as the web app reads it.
/// </summary>
public class SharedChatModePostgresTests
{
    private static async Task<(PostgresSharingDatabase Database, SessionAccessService Access, SessionSharingService Sharing)> CreateAsync()
    {
        var database = await PostgresSharingDatabase.CreateAsync();
        foreach (var user in new[] { "alice", "bob", "carol" })
            await database.AddUserAsync(user);
        var license = new FakeEnterpriseLicense(true);
        return (database, new SessionAccessService(database.Shares, license),
            new SessionSharingService(database.Shares, license, "https://hub.example.com"));
    }

    private static Task AddAsync(PostgresSharingDatabase database, string id, string uiMode,
        string? parent = null, string? project = null)
        => database.UpsertSessionAsync(new SessionRecord
        {
            Id = id, Owner = "alice", Title = id, Mode = SessionMode.Interactive, Agent = AgentKind.Claude,
            UiMode = uiMode, ParentSessionId = parent, ProjectId = project,
            ClaudeSessionId = $"claude-{id}", CallbackToken = $"callback-{id}"
        });

    [PostgreSqlFact]
    public async Task NamedShare_ReportsTheSessionsUiModeInTheListingAndTheResolvedView()
    {
        var (database, access, sharing) = await CreateAsync();
        await using var _ = database;
        await AddAsync(database, "chat", SessionUiMode.Chat);
        await AddAsync(database, "term", SessionUiMode.Terminal);
        await database.Shares.UpsertDirectAsync("alice", "chat", "bob", ShareRole.Collaborator);
        await database.Shares.UpsertDirectAsync("alice", "term", "bob", ShareRole.Collaborator);
        await database.Shares.UpsertDirectAsync("alice", "chat", "carol", ShareRole.Viewer);

        var listed = (await sharing.ListSharedWithAsync("bob")).ToDictionary(s => s.Id);
        Assert.Equal(SessionUiMode.Chat, listed["chat"].UiMode);
        Assert.Equal(SessionUiMode.Terminal, listed["term"].UiMode);
        Assert.True(listed["chat"].CanWrite);

        var bob = SharedSessionSanitizer.Sanitize((await access.ResolveUserAsync("bob", "chat"))!);
        Assert.Equal(SessionUiMode.Chat, bob.UiMode);
        Assert.Equal("Collaborator", bob.AccessRole);

        // The transport follows the session; the role still decides who may send on it.
        var carol = SharedSessionSanitizer.Sanitize((await access.ResolveUserAsync("carol", "chat"))!);
        Assert.Equal(SessionUiMode.Chat, carol.UiMode);
        Assert.False(carol.CanWrite);
    }

    [PostgreSqlFact]
    public async Task InheritedAndProjectAccess_ReportTheChildsOwnUiMode()
    {
        var (database, access, sharing) = await CreateAsync();
        await using var _ = database;
        var project = (await database.Projects.CreateAsync("alice", new CreateProjectRequest("pairing", null))).Id;
        await AddAsync(database, "parent", SessionUiMode.Terminal);
        await AddAsync(database, "child", SessionUiMode.Chat, parent: "parent");
        await AddAsync(database, "in-project", SessionUiMode.Chat, project: project);
        await database.Shares.UpsertDirectAsync("alice", "parent", "bob", ShareRole.Collaborator);
        await database.Shares.UpsertProjectShareAsync("alice", project, ProjectShareSubjectType.User, "carol", ShareRole.Collaborator);

        var bobs = (await sharing.ListSharedWithAsync("bob")).ToDictionary(s => s.Id);
        Assert.Equal(SessionUiMode.Terminal, bobs["parent"].UiMode);
        Assert.Equal(SessionUiMode.Chat, bobs["child"].UiMode);
        Assert.Equal(SessionUiMode.Chat, SharedSessionSanitizer.Sanitize((await access.ResolveUserAsync("bob", "child"))!).UiMode);

        var carols = (await sharing.ListSharedWithAsync("carol")).ToDictionary(s => s.Id);
        Assert.Equal(SessionUiMode.Chat, carols["in-project"].UiMode);
        Assert.Equal(SessionUiMode.Chat, SharedSessionSanitizer.Sanitize((await access.ResolveUserAsync("carol", "in-project"))!).UiMode);
    }

    [PostgreSqlFact]
    public async Task CollaboratorLink_ReportsTheSessionsUiMode()
    {
        var (database, access, _) = await CreateAsync();
        await using var __ = database;
        await AddAsync(database, "chat", SessionUiMode.Chat);
        var link = await database.Shares.CreateLinkAsync("alice", "chat", ShareRole.Collaborator, null);

        var resolved = await access.ResolveTokenAsync(link.Token);
        Assert.NotNull(resolved);
        var view = SharedSessionSanitizer.Sanitize(resolved);
        Assert.Equal(SessionUiMode.Chat, view.UiMode);
        Assert.True(view.CanWrite);
    }
}
