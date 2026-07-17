using System.Text.RegularExpressions;

namespace AgentHub.Api.Models;

public sealed record ProjectInfo(string Id, string Name, string? Color, int SortOrder);

public sealed record CreateProjectRequest(string Name, string? Color);

public sealed record UpdateProjectRequest(string? Name, string? Color, int? SortOrder);

public static class ProjectValidation
{
    private static readonly Regex HexColor = new("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant);

    public static bool IsValidName(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 80;

    public static bool IsValidColor(string? value)
        => value is null || HexColor.IsMatch(value);
}
