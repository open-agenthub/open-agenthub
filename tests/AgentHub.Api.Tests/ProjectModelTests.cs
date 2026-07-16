using AgentHub.Api.Models;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class ProjectModelTests
{
    [Theory]
    [InlineData("Project", true)]
    [InlineData("", false)]
    [InlineData("   ", false)]
    public void ProjectNameValidation_IsDeterministic(string name, bool expected)
        => Assert.Equal(expected, ProjectValidation.IsValidName(name));

    [Theory]
    [InlineData(null, true)]
    [InlineData("#12abEF", true)]
    [InlineData("red", false)]
    public void ProjectColorValidation_AcceptsOnlyHex(string? color, bool expected)
        => Assert.Equal(expected, ProjectValidation.IsValidColor(color));
}
