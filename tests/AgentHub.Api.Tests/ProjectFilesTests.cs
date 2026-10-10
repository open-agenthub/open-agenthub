using AgentHub.Api.Ee.Sharing;
using AgentHub.Api.Files;
using AgentHub.Api.Persistence;
using AgentHub.Api.Storage;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// A session may read the files of the sessions that share its owner and its project, and of no
/// other. These run the real authoriser and the real rule against a store that — unlike the
/// Postgres one — answers a lookup for any owner, so a test passes because the rule refused and
/// not because the query happened to be scoped.
/// </summary>
public sealed class ProjectFilesTests
{
    private const string FileId = "0123456789abcdef0123456789abcdef";

    [Fact]
    public async Task A_sibling_in_the_same_project_of_the_same_owner_is_listed_and_readable()
    {
        var world = new World();

        var listing = await world.ListAsync("caller");
        var content = await world.Controller("caller")
            .ProjectFileContent("caller", "sibling", FileId, default);

        var file = Assert.Single(listing.Files);
        Assert.Equal("sibling", file.SessionId);
        Assert.Equal("Screenshots", file.SessionTitle);
        Assert.Equal("shot.png", file.Name);
        Assert.False(listing.Truncated);
        var stream = Assert.IsType<FileStreamResult>(content);
        await using var body = stream.FileStream;
        using var buffer = new MemoryStream();
        await body.CopyToAsync(buffer);
        Assert.Equal("sibling"u8.ToArray(), buffer.ToArray());
        // The pod's fetch would follow a redirect carrying X-Agent-Token to the storage endpoint.
        Assert.False(world.Files.LastAllowRedirect);
    }

    [Fact]
    public async Task The_listing_holds_only_siblings_and_never_the_caller_s_own_files()
    {
        var world = new World();

        var listing = await world.ListAsync("caller");

        Assert.Equal(["sibling"], listing.Files.Select(file => file.SessionId).Distinct());
    }

    [Theory]
    [InlineData("other-owner")]
    [InlineData("other-project")]
    [InlineData("no-project")]
    [InlineData("deleted")]
    [InlineData("never-existed")]
    [InlineData("caller")]
    public async Task Every_refusal_is_the_same_bare_404(string target)
    {
        var world = new World();

        var content = await world.Controller("caller")
            .ProjectFileContent("caller", target, FileId, default);
        var narrowed = await world.Controller("caller").ProjectFiles("caller", target, default);

        // NotFoundResult, not NotFoundObjectResult: a body on one kind of refusal and none on
        // another is as good as a different status for telling which ids exist.
        Assert.IsType<NotFoundResult>(content);
        Assert.IsType<NotFoundResult>(narrowed);
        Assert.Empty(world.Files.Opened);
    }

    [Fact]
    public async Task An_unknown_file_in_a_sibling_is_the_same_404_as_a_refused_session()
    {
        var world = new World();

        var result = await world.Controller("caller").ProjectFileContent(
            "caller", "sibling", "ffffffffffffffffffffffffffffffff", default);

        Assert.IsType<NotFoundResult>(result);
    }

    [Fact]
    public async Task A_session_without_a_project_has_no_siblings_not_even_other_unsorted_ones()
    {
        // "No project" is not a project. Two sessions that both have none must not find each
        // other, or every session an owner never sorted would share one pool.
        var world = new World();
        world.Store.Add(World.Session("no-project-2", "alice", null));

        var listing = await world.ListAsync("no-project");
        var content = await world.Controller("no-project")
            .ProjectFileContent("no-project", "no-project-2", FileId, default);

        Assert.Empty(listing.Files);
        Assert.IsType<NotFoundResult>(content);
    }

    [Fact]
    public async Task An_empty_project_id_is_no_project_either()
    {
        var world = new World();
        world.Store.Add(World.Session("blank-1", "alice", ""));
        world.Store.Add(World.Session("blank-2", "alice", ""));

        Assert.Empty((await world.ListAsync("blank-1")).Files);
        Assert.IsType<NotFoundResult>(await world.Controller("blank-1")
            .ProjectFileContent("blank-1", "blank-2", FileId, default));
    }

    [Fact]
    public async Task The_same_project_id_under_another_owner_is_another_project()
    {
        var world = new World();

        var listing = await world.ListAsync("other-owner");

        Assert.Empty(listing.Files);
    }

    [Fact]
    public async Task A_token_reads_project_files_only_through_its_own_session_s_route()
    {
        // The caller is whoever the token says, never whoever the path says: the sibling's id in
        // the path with the caller's token must not turn the caller into the sibling.
        var world = new World();

        var result = await world.Controller("caller").ProjectFiles("sibling", null, default);
        var content = await world.Controller("caller")
            .ProjectFileContent("sibling", "caller", FileId, default);

        Assert.IsType<UnauthorizedResult>(result);
        Assert.IsType<UnauthorizedResult>(content);
    }

    [Fact]
    public async Task Without_a_token_nothing_is_listed()
    {
        var world = new World();

        Assert.IsType<UnauthorizedResult>(
            await world.Controller(null).ProjectFiles("caller", null, default));
    }

    [Fact]
    public async Task A_sibling_s_files_are_read_as_an_actor_that_can_neither_write_nor_manage()
    {
        var world = new World();

        await world.ListAsync("caller");
        var content = Assert.IsType<FileStreamResult>(await world.Controller("caller")
            .ProjectFileContent("caller", "sibling", FileId, default));
        await content.FileStream.DisposeAsync();

        Assert.NotEmpty(world.Files.Actors);
        Assert.All(world.Files.Actors, actor =>
        {
            Assert.Equal("sibling", actor.SessionId);
            Assert.False(actor.CanWrite);
            Assert.False(actor.CanManage);
        });
    }

    [Fact]
    public async Task A_session_that_is_shared_with_anyone_reads_no_sibling_files()
    {
        // The share was granted for this one session. Whoever holds it can type into the terminal
        // or watch it, so what the agent can fetch is what they can get at.
        var world = new World();
        world.Shares.Shared.Add("caller");

        var listing = await world.ListAsync("caller");
        var content = await world.Controller("caller")
            .ProjectFileContent("caller", "sibling", FileId, default);
        var narrowed = await world.Controller("caller").ProjectFiles("caller", "sibling", default);

        Assert.Empty(listing.Files);
        Assert.IsType<NotFoundResult>(content);
        Assert.IsType<NotFoundResult>(narrowed);
        Assert.Empty(world.Files.Opened);
    }

    [Fact]
    public async Task Sharing_the_source_session_does_not_stop_its_siblings_from_reading_it()
    {
        // The refusal is about who can drive the reader. A viewer of the screenshot session gains
        // nothing from an unshared sibling reading its files.
        var world = new World();
        world.Shares.Shared.Add("sibling");

        Assert.Single((await world.ListAsync("caller")).Files);
    }

    [Fact]
    public async Task The_caller_s_own_files_are_untouched_by_a_share()
    {
        var world = new World();
        world.Shares.Shared.Add("caller");

        var result = Assert.IsType<OkObjectResult>(await world.Controller("caller").List("caller", default));

        Assert.Single(Assert.IsAssignableFrom<IEnumerable<SessionFileResponse>>(result.Value));
    }

    [Fact]
    public async Task Files_that_are_not_ready_are_not_listed()
    {
        var world = new World();
        world.Files.Add("sibling", World.File("sibling", "11111111111111111111111111111111") with
        {
            State = SessionFileState.Reserved,
        });

        var listing = await world.ListAsync("caller");

        Assert.Equal([FileId], listing.Files.Select(file => file.Id));
    }

    [Fact]
    public async Task A_listing_too_long_for_the_pod_s_client_is_cut_short_and_says_so()
    {
        var world = new World();
        for (var index = 0; index < InternalSessionFilesController.MaxProjectFiles; index++)
        {
            world.Files.Add("sibling", World.File("sibling", index.ToString("x32")));
        }

        var listing = await world.ListAsync("caller");

        Assert.Equal(InternalSessionFilesController.MaxProjectFiles, listing.Files.Count);
        Assert.True(listing.Truncated);
    }

    [Theory]
    [InlineData("alice", "p1", "alice", "p1", true)]
    [InlineData("alice", "p1", "bob", "p1", false)]
    [InlineData("alice", "p1", "alice", "p2", false)]
    [InlineData("alice", "p1", "alice", null, false)]
    [InlineData("alice", null, "alice", null, false)]
    [InlineData("alice", "", "alice", "", false)]
    [InlineData("alice", "p1", "alice", "P1", false)]
    [InlineData("alice", "p1", "Alice", "p1", false)]
    public void The_rule(
        string callerOwner, string? callerProject,
        string otherOwner, string? otherProject, bool expected)
    {
        Assert.Equal(expected, ProjectFileAccess.IsSibling(
            World.Session("a", callerOwner, callerProject),
            World.Session("b", otherOwner, otherProject)));
    }

    [Fact]
    public void A_session_is_not_its_own_sibling()
    {
        var session = World.Session("a", "alice", "p1");

        Assert.False(ProjectFileAccess.IsSibling(session, session));
    }

    private sealed class World
    {
        public MemorySessions Store { get; } = new();
        public MemoryFiles Files { get; } = new();
        public MemoryShares Shares { get; } = new();

        public World()
        {
            Store.Add(Session("caller", "alice", "p1", "Caller"));
            Store.Add(Session("sibling", "alice", "p1", "Screenshots"));
            Store.Add(Session("deleted", "alice", "p1"));
            Store.Add(Session("other-project", "alice", "p2"));
            Store.Add(Session("no-project", "alice", null));
            Store.Add(Session("other-owner", "bob", "p1"));
            foreach (var id in new[]
                     {
                         "caller", "sibling", "deleted", "other-project", "no-project", "other-owner",
                     })
            {
                Files.Add(id, File(id, FileId));
            }
            // Gone from the store while its file rows are still there, which is the order a real
            // deletion goes through: the listing must not resurrect it from the files side.
            Store.DeleteAsync("deleted").GetAwaiter().GetResult();
        }

        public static SessionRecord Session(
            string id, string owner, string? project, string? title = null) => new()
            {
                Id = id,
                Owner = owner,
                ProjectId = project,
                Title = title ?? id,
                CallbackToken = $"token-{id}",
            };

        public static SessionFileRecord File(string sessionId, string id) => new(
            id, sessionId, "unused", "shot.png", ".png", "image/png", "image/png", 3,
            SessionFileStorageKind.S3, $"{sessionId}/{id}", SessionFileState.Ready,
            SessionFilePreviewState.None, null, "agent", "agent", DateTime.UtcNow,
            DateTime.UtcNow, null);

        public InternalSessionFilesController Controller(string? tokenOf)
        {
            var context = new DefaultHttpContext();
            if (tokenOf is not null) context.Request.Headers["X-Agent-Token"] = $"token-{tokenOf}";
            return new InternalSessionFilesController(
                new AgentCallbackAuthorizer(Store), Files, new NullArtifactStore(),
                new SessionFileOptions(), new ProjectFileAccess(Store, Shares))
            {
                ControllerContext = new ControllerContext { HttpContext = context },
            };
        }

        public async Task<ProjectSessionFilesResponse> ListAsync(string caller)
        {
            var result = Assert.IsType<OkObjectResult>(
                await Controller(caller).ProjectFiles(caller, null, default));
            return Assert.IsType<ProjectSessionFilesResponse>(result.Value);
        }
    }

    /// <summary>
    /// Deliberately ignores the owner argument of <see cref="GetAsync"/> and
    /// <see cref="ListAsync"/>. The real store scopes both in SQL, which would hide a rule that
    /// forgot to compare owners; this one hands back whatever was asked for.
    /// </summary>
    private sealed class MemorySessions : ISessionStore
    {
        private readonly Dictionary<string, SessionRecord> _records = new(StringComparer.Ordinal);

        public void Add(SessionRecord record) => _records[record.Id] = record;
        public Task InitializeAsync(CancellationToken ct = default) => Task.CompletedTask;
        public Task UpsertAsync(SessionRecord r, CancellationToken ct = default)
        {
            Add(r);
            return Task.CompletedTask;
        }
        public Task<SessionRecord?> GetAsync(string owner, string id, CancellationToken ct = default) =>
            Task.FromResult(_records.GetValueOrDefault(id));
        public Task<SessionRecord?> GetByCallbackTokenAsync(string token, CancellationToken ct = default) =>
            Task.FromResult(_records.Values.FirstOrDefault(record => record.CallbackToken == token));
        public Task<IReadOnlyList<SessionRecord>> ListAsync(string owner, CancellationToken ct = default) =>
            Task.FromResult<IReadOnlyList<SessionRecord>>(_records.Values.ToArray());
        public Task UpdateStatusAsync(string id, string status, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetQuestionPendingAsync(string id, bool pending, CancellationToken ct = default) => Task.CompletedTask;
        public Task SetScrollbackAsync(string id, string text, CancellationToken ct = default) => Task.CompletedTask;
        public Task<string?> GetScrollbackAsync(string id, CancellationToken ct = default) => Task.FromResult<string?>(null);
        public Task DeleteAsync(string id, CancellationToken ct = default)
        {
            _records.Remove(id);
            return Task.CompletedTask;
        }
    }

    private sealed class MemoryShares : ISessionShareStatus
    {
        public HashSet<string> Shared { get; } = new(StringComparer.Ordinal);
        public Task<bool> IsSharedAsync(string sessionId, CancellationToken ct = default) =>
            Task.FromResult(Shared.Contains(sessionId));
    }

    private sealed class MemoryFiles : ISessionFileService
    {
        private readonly Dictionary<string, List<SessionFileRecord>> _files = new(StringComparer.Ordinal);

        public List<SessionFileActor> Actors { get; } = [];
        public List<string> Opened { get; } = [];
        public bool? LastAllowRedirect { get; private set; }

        public void Add(string sessionId, SessionFileRecord file)
        {
            if (!_files.TryGetValue(sessionId, out var list)) _files[sessionId] = list = [];
            list.Add(file);
        }

        public Task<IReadOnlyList<SessionFileRecord>> ListAsync(SessionFileActor actor, CancellationToken ct = default)
        {
            Actors.Add(actor);
            return Task.FromResult<IReadOnlyList<SessionFileRecord>>(
                _files.TryGetValue(actor.SessionId, out var list) ? list.ToArray() : []);
        }

        public Task<AgentHub.Api.Files.FileContentResult> OpenContentAsync(SessionFileActor actor, string fileId, bool allowRedirect = true, CancellationToken ct = default)
        {
            Actors.Add(actor);
            LastAllowRedirect = allowRedirect;
            var file = _files.GetValueOrDefault(actor.SessionId)?.FirstOrDefault(item => item.Id == fileId)
                ?? throw new SessionFileException("file_not_found");
            Opened.Add($"{actor.SessionId}/{fileId}");
            return Task.FromResult(new AgentHub.Api.Files.FileContentResult(
                new MemoryStream(System.Text.Encoding.UTF8.GetBytes(actor.SessionId)),
                null, "image/png", file.Name, file.Size));
        }

        public Task<ReserveFileResult> ReserveAsync(SessionFileActor actor, ReserveSessionFileCommand request, CancellationToken ct = default) => throw new NotSupportedException();
        public Task PutPodContentAsync(SessionFileActor actor, string fileId, Stream content, long? contentLength = null, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionFileRecord> CompleteAsync(SessionFileActor actor, string fileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteAsync(SessionFileActor actor, string fileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionFilePresentation?> GetPresentationAsync(SessionFileActor actor, CancellationToken ct = default) => throw new NotSupportedException();
        public Task<SessionFilePresentation> SetPresentationAsync(SessionFileActor actor, string? fileId, CancellationToken ct = default) => throw new NotSupportedException();
        public Task DeleteSessionFilesAsync(string sessionId, CancellationToken ct = default) => throw new NotSupportedException();
    }
}
