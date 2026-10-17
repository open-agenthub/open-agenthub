using System.Text.RegularExpressions;

namespace AgentHub.Api.Models;

/// <summary>A project as its owner sees it.</summary>
/// <param name="SecretIds">The stored secrets the project's sessions receive and the most they can
/// narrow to (docs/session-secrets.md); empty = none.</param>
public sealed record ProjectInfo(string Id, string Name, string? Color, int SortOrder,
    IReadOnlyList<string>? SecretIds = null);

public sealed record CreateProjectRequest(string Name, string? Color, List<string>? SecretIds = null);

/// <summary>A partial project update; every null field is left as it is.</summary>
/// <param name="SecretIds">Replaces the secret selection; null = unchanged, empty = none.</param>
public sealed record UpdateProjectRequest(string? Name, string? Color, int? SortOrder, List<string>? SecretIds = null);

public static class ProjectValidation
{
    private static readonly Regex HexColor = new("^#[0-9a-fA-F]{6}$", RegexOptions.CultureInvariant);

    public static bool IsValidName(string? value)
        => !string.IsNullOrWhiteSpace(value) && value.Trim().Length <= 80;

    public static bool IsValidColor(string? value)
        => value is null || HexColor.IsMatch(value);
}
