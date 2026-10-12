using AgentHub.Api.Controllers;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class RemoteGitControllerTests
{
    private sealed class Git : IGitAuthService
    {
        public string? Owner;
        public string? Query;
        public Exception? Failure;
        public int Calls;
        public bool AnyConfigured => true;
        public bool IsConfigured(string id) => id == "gitlab-work";
        public Task<IReadOnlyList<GitProviderInfo>> ListProvidersAsync(string owner, CancellationToken ct = default)
        {
            Calls++; Owner = owner;
            return Task.FromResult<IReadOnlyList<GitProviderInfo>>([new() {Id="gitlab-work",Type="gitlab",DisplayName="Work",Connected=true}]);
        }
        public Task<IReadOnlyList<GitProject>> SearchProjectsAsync(string owner, string provider, string? query, CancellationToken ct = default)
        {
            Calls++; Owner = owner; Query = query;
            if (Failure is not null) throw Failure;
            return Task.FromResult<IReadOnlyList<GitProject>>([new(){Name="repo",FullName="team/repo",Url="https://gitlab.example/team/repo.git",ProviderId=provider}]);
        }
        public string CreateAuthorizeUrl(string p, string o, string r) => throw new NotSupportedException();
        public Task<string?> HandleCallbackAsync(string p,string code,string state,string redirect,CancellationToken ct=default) => throw new NotSupportedException();
        public Task DisconnectAsync(string owner,string provider,CancellationToken ct=default) => throw new NotSupportedException();
        public Task<bool> IsConnectedAsync(string owner,string provider,CancellationToken ct=default) => throw new NotSupportedException();
        public Task<string?> BuildCredentialStoreAsync(string owner,IEnumerable<RepoRef> repos,CancellationToken ct=default) => throw new NotSupportedException();
    }
    private static RemoteGitController Controller(Git git, string? header = "Bearer oah_valid", ApiTokenScope? scope = null, string owner = "alice")
    {
        var controller = new RemoteGitController((token,ct) => Task.FromResult(token=="oah_valid"?new RemoteCaller(owner,scope):null),git)
        { ControllerContext = new ControllerContext {HttpContext=new DefaultHttpContext()} };
        if(header is not null) controller.Request.Headers.Authorization=header;
        return controller;
    }
    [Theory]
    [InlineData(null)] [InlineData("Bearer jwt")] [InlineData("Bearer oah_invalid")] [InlineData("Basic oah_valid")]
    public async Task InvalidAuthNeverCallsGit(string? header)
    {
        var git=new Git(); var c=Controller(git,header);
        Assert.IsType<UnauthorizedResult>((await c.Providers(default)).Result);
        Assert.IsType<UnauthorizedResult>((await c.Projects("gitlab-work",null,default)).Result);
        Assert.Equal(0,git.Calls);
    }
    [Theory]
    [InlineData("alice")] [InlineData("bob")]
    public async Task UnrestrictedTokenUsesOnlyResolvedOwner(string owner)
    {
        var git=new Git(); var c=Controller(git,owner:owner);
        Assert.IsType<OkObjectResult>((await c.Providers(default)).Result);
        var result=Assert.IsType<OkObjectResult>((await c.Projects("gitlab-work","team",default)).Result);
        Assert.Single(Assert.IsAssignableFrom<IReadOnlyList<GitProject>>(result.Value));
        Assert.Equal(owner,git.Owner); Assert.Equal("team",git.Query);
    }
    [Fact]
    public async Task RestrictedTokenCannotAcquireOAuthThroughGitPatWildcard()
    {
        foreach(var scope in new[]{new ApiTokenScope(),new ApiTokenScope{GitPats=["*"]}})
        {
            var git=new Git(); var c=Controller(git,scope:scope);
            foreach(var result in new[]{(await c.Providers(default)).Result,(await c.Projects("gitlab-work",null,default)).Result})
            {
                var denied=Assert.IsType<ObjectResult>(result);
                Assert.Equal(403,denied.StatusCode); Assert.Equal(RemoteGitController.OAuthNotAllowed,denied.Value);
            }
            Assert.Equal(0,git.Calls);
        }
    }
    [Fact]
    public async Task ArbitraryProviderUrlAndOversizedQueryNeverReachUpstream()
    {
        var git=new Git(); var c=Controller(git);
        Assert.IsType<NotFoundResult>((await c.Projects("https://attacker.test",null,default)).Result);
        Assert.IsType<BadRequestObjectResult>((await c.Projects("gitlab-work",new string('x',257),default)).Result);
        Assert.IsType<BadRequestObjectResult>((await c.Projects("",null,default)).Result);
        Assert.Equal(0,git.Calls);
    }
    [Fact]
    public async Task UpstreamExceptionsDoNotExposeSecrets()
    {
        foreach(var error in new Exception[]{new HttpRequestException("secret"),new InvalidOperationException("secret"),new ArgumentException("secret")})
        {
            var result=Assert.IsAssignableFrom<ObjectResult>((await Controller(new Git{Failure=error}).Projects("gitlab-work",null,default)).Result);
            Assert.DoesNotContain("secret",Assert.IsType<string>(result.Value));
        }
    }
}
