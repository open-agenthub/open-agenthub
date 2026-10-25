using System.Text.Json;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public class AgentPolicyMatcherTests
{
    private static readonly AgentPolicy Policy = new()
    {
        AllowedTools = ["Read", "local_read_*"],
        AllowedMcpTools = ["mcp__docs__search", "mcp__files__*"],
        AllowedCommands = ["git status", "dotnet test"]
    };

    [Theory]
    [InlineData("git status", "allow")]
    [InlineData("git status --short", "allow")]
    [InlineData("git push", "deny")]
    [InlineData("git status && rm -rf /", "deny")]
    [InlineData("git status || rm -rf /", "deny")]
    [InlineData("git status; rm -rf /", "deny")]
    [InlineData("git status | cat", "deny")]
    [InlineData("git status > output", "deny")]
    [InlineData("echo $(id)", "deny")]
    [InlineData("echo `id`", "deny")]
    [InlineData("echo $HOME", "deny")]
    [InlineData("HOME=/tmp git status", "deny")]
    [InlineData("git status *", "deny")]
    [InlineData("git status \\\"unterminated", "deny")]
    [InlineData("git status \\\\--short", "deny")]
    [InlineData("", "deny")]
    public void Decide_ParsesEveryCommandComponentConservatively(string command, string expected)
    {
        using var input = JsonDocument.Parse(JsonSerializer.Serialize(new { command }));

        var decision = AgentPolicyMatcher.Decide(Policy, "Bash", input.RootElement);

        Assert.Equal(expected, decision.Decision);
        if (command.Length > 0)
            Assert.DoesNotContain(command, decision.Reason, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("Read", "allow")]
    [InlineData("read", "deny")]
    [InlineData("local_read_file", "allow")]
    [InlineData("local_write_file", "deny")]
    [InlineData("mcp__docs__search", "allow")]
    [InlineData("mcp__docs__search_more", "deny")]
    [InlineData("mcp__files__read", "allow")]
    [InlineData("mcp__Files__read", "deny")]
    public void Decide_MatchesExactAndTrailingWildcardToolsOrdinally(string tool, string expected)
    {
        using var input = JsonDocument.Parse("{}");

        var decision = AgentPolicyMatcher.Decide(Policy, tool, input.RootElement);

        Assert.Equal(expected, decision.Decision);
    }

    [Theory]
    [InlineData("[]")]
    [InlineData("null")]
    [InlineData("{\"command\":17}")]
    [InlineData("{\"command\":{\"value\":\"git status\"}}")]
    public void Decide_DeniesMaliciousOrMalformedBashInput(string json)
    {
        using var input = JsonDocument.Parse(json);

        Assert.Equal("deny", AgentPolicyMatcher.Decide(Policy, "Bash", input.RootElement).Decision);
    }

    [Fact]
    public void Decide_EmptyPolicyIsDefaultDeny()
    {
        using var input = JsonDocument.Parse("{}");

        Assert.Equal("deny", AgentPolicyMatcher.Decide(new AgentPolicy(), "Read", input.RootElement).Decision);
        Assert.Equal("deny", AgentPolicyMatcher.Decide(new AgentPolicy(), "mcp__docs__search", input.RootElement).Decision);
    }

    [Theory]
    [InlineData("*")]
    [InlineData("mcp__docs**")]
    [InlineData("mcp__docs*search")]
    public void Decide_IgnoresUnboundedOrMalformedWildcardPatterns(string pattern)
    {
        using var input = JsonDocument.Parse("{}");
        var policy = new AgentPolicy { AllowedMcpTools = [pattern] };

        Assert.Equal("deny", AgentPolicyMatcher.Decide(policy, "mcp__docs__search", input.RootElement).Decision);
    }

    // An auto-approving session is one the owner asked to run unattended, so a tool outside the
    // allow list is a question for the approval endpoint rather than a refusal here. Deciding it
    // here is what made auto-approve unreachable: the flag lives behind /permission, and an
    // unattended session that never got there was stopped by its own empty allow list.
    [Theory]
    [InlineData("Read", "allow")]
    [InlineData("WebFetch", "ask")]
    [InlineData("mcp__docs__search", "allow")]
    [InlineData("mcp__other__search", "ask")]
    public void Decide_LeavesUncoveredToolsToApprovalWhenTheSessionAutoApproves(
        string tool, string expected)
    {
        using var input = JsonDocument.Parse("{}");

        Assert.Equal(expected,
            AgentPolicyMatcher.Decide(Policy, tool, input.RootElement, autoApprove: true).Decision);
    }

    [Theory]
    [InlineData("git status", "allow")]
    [InlineData("git push", "ask")]
    public void Decide_LeavesUncoveredCommandsToApprovalWhenTheSessionAutoApproves(
        string command, string expected)
    {
        using var input = JsonDocument.Parse(JsonSerializer.Serialize(new { command }));

        Assert.Equal(expected,
            AgentPolicyMatcher.Decide(Policy, "Bash", input.RootElement, autoApprove: true).Decision);
    }

    [Fact]
    public void Decide_EmptyCommandPolicyIsStillOpenToApproval()
    {
        using var input = JsonDocument.Parse(JsonSerializer.Serialize(new { command = "curl https://example.com" }));

        Assert.Equal("ask", AgentPolicyMatcher
            .Decide(new AgentPolicy(), "Bash", input.RootElement, autoApprove: true).Decision);
    }

    // #244: the parser only exists to stop an allowed prefix from carrying a second command, so a
    // command it cannot take apart is not covered rather than malformed. Denying it stopped an
    // autonomous Codex smoke session at its first command; the redirect case is that command
    // verbatim. Without auto-approve the verdict stays deny.
    [Theory]
    [InlineData("echo smoke > smoke.txt && cat smoke.txt && uname -m")]
    [InlineData("echo \"$HOME\" | wc -c")]
    [InlineData("ls *.md")]
    [InlineData("echo $(id)")]
    [InlineData("echo `id`")]
    [InlineData("git status \"unterminated")]
    [InlineData("HOME=/tmp git status")]
    [InlineData("printf ok > a.txt\ncat a.txt")]
    public void Decide_LeavesCommandsTheParserCannotReadToApprovalUnderAutoApprove(string command)
    {
        using var input = JsonDocument.Parse(JsonSerializer.Serialize(new { command }));

        Assert.Equal("ask",
            AgentPolicyMatcher.Decide(Policy, "Bash", input.RootElement, autoApprove: true).Decision);
        Assert.Equal("deny", AgentPolicyMatcher.Decide(Policy, "Bash", input.RootElement).Decision);
    }

    // Input that is not a shell command at all is not a question anyone can be asked to approve.
    [Theory]
    [InlineData("{\"command\":17}")]
    [InlineData("{\"command\":\"  \"}")]
    [InlineData("{}")]
    [InlineData("[]")]
    public void Decide_NeverSoftensInputThatIsNotACommand(string json)
    {
        using var input = JsonDocument.Parse(json);

        Assert.Equal("deny",
            AgentPolicyMatcher.Decide(Policy, "Bash", input.RootElement, autoApprove: true).Decision);
    }

    // The consequence of the choice above, written down so it cannot change by accident: under
    // auto-approve a chain whose components are not all covered reaches the approval endpoint,
    // exactly as a human clicking "allow" would have let it through.
    [Fact]
    public void Decide_AsksRatherThanDeniesAChainedCommandUnderAutoApprove()
    {
        using var input = JsonDocument.Parse(
            JsonSerializer.Serialize(new { command = "git status && git push" }));

        Assert.Equal("ask",
            AgentPolicyMatcher.Decide(Policy, "Bash", input.RootElement, autoApprove: true).Decision);
        Assert.Equal("deny",
            AgentPolicyMatcher.Decide(Policy, "Bash", input.RootElement).Decision);
    }

    [Fact]
    public void Decide_DeniesAMissingToolNameEvenWhenTheSessionAutoApproves()
    {
        using var input = JsonDocument.Parse("{}");

        Assert.Equal("deny",
            AgentPolicyMatcher.Decide(Policy, "  ", input.RootElement, autoApprove: true).Decision);
    }
}
