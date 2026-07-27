using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public class SessionDescentTests
{
    [Fact]
    public void IsDescendant_WalksParentChain()
    {
        var byId = new Dictionary<string, string?>
        {
            ["root"] = null,
            ["a"] = "root",
            ["b"] = "a",
            ["other"] = null
        };
        Assert.True(SessionDescent.IsDescendant("b", "root", id => byId.GetValueOrDefault(id)));
        Assert.False(SessionDescent.IsDescendant("other", "root", id => byId.GetValueOrDefault(id)));
    }

    [Fact]
    public void IsDescendant_DirectChild_IsTrue()
    {
        var byId = new Dictionary<string, string?>
        {
            ["root"] = null,
            ["child"] = "root"
        };
        Assert.True(SessionDescent.IsDescendant("child", "root", id => byId.GetValueOrDefault(id)));
    }

    [Fact]
    public void IsDescendant_SameId_IsFalse()
    {
        var byId = new Dictionary<string, string?> { ["root"] = null };
        Assert.False(SessionDescent.IsDescendant("root", "root", id => byId.GetValueOrDefault(id)));
    }

    [Fact]
    public void IsDescendant_Sibling_IsFalse()
    {
        var byId = new Dictionary<string, string?>
        {
            ["root"] = null,
            ["a"] = "root",
            ["b"] = "root"
        };
        Assert.False(SessionDescent.IsDescendant("b", "a", id => byId.GetValueOrDefault(id)));
    }

    [Fact]
    public void IsDescendant_Cycle_ReturnsFalse()
    {
        var byId = new Dictionary<string, string?>
        {
            ["a"] = "b",
            ["b"] = "a"
        };
        Assert.False(SessionDescent.IsDescendant("a", "root", id => byId.GetValueOrDefault(id)));
    }

    [Fact]
    public void IsDescendant_LongChainWithoutAncestor_ReturnsFalse()
    {
        var byId = new Dictionary<string, string?>();
        for (var i = 0; i < 100; i++)
            byId[$"n{i}"] = $"n{i + 1}";
        byId["n100"] = "n0"; // cycle past the hop limit
        Assert.False(SessionDescent.IsDescendant("n0", "missing", id => byId.GetValueOrDefault(id)));
    }
}
