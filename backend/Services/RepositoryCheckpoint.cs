using System.Text.Json;
using System.Security.Cryptography;
using System.Text.RegularExpressions;

namespace AgentHub.Api.Services;

public sealed record RepositoryOmissions(int Count, IReadOnlyList<string> Names, int Omitted);
public sealed record RepositoryCheckpointEntry(int Index, string Destination, string? Branch, string? Commit,
    string? RemoteBranch, string? RemoteTip, int? TrackedChanges, int? Untracked, RepositoryOmissions? Ignored,
    int? Unpushed, string? Error);
public sealed record RepositoryCheckpoint(int Version, string Strategy,
    IReadOnlyList<RepositoryCheckpointEntry> Repositories, RepositoryOmissions Outside)
{
    public string ReviewHash => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(
        new { Version, Strategy, Repositories, Outside })));

    public void Validate(bool requireReady = true)
    {
        if (Version != 1 || Strategy != "repository" || Repositories.Count is < 1 or > 32
            || JsonSerializer.SerializeToUtf8Bytes(this).Length > 65536)
            throw new ProviderReplacementRefusedException("The repository checkpoint is invalid or exceeds its limit.");
        ValidateOmissions(Outside);
        var paths = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < Repositories.Count; index++)
        {
            var item = Repositories[index];
            if (item.Index != index || !SafePath(item.Destination) || !paths.Add(item.Destination))
                throw new ProviderReplacementRefusedException("The repository layout cannot be restored safely.");
            if (item.Ignored is not null) ValidateOmissions(item.Ignored);
            if (!requireReady && item.Error is not null) continue;
            if (item.Error is not null || item.TrackedChanges != 0 || item.Untracked != 0 || item.Unpushed != 0)
                throw new ProviderReplacementRefusedException("Save and push repository changes, then check again before reviewing the switch.");
            if (!SafeRef(item.Branch) || item.RemoteBranch is null || !item.RemoteBranch.StartsWith("refs/heads/", StringComparison.Ordinal)
                || !SafeRef(item.RemoteBranch[11..]) || !CommitId(item.Commit) || !CommitId(item.RemoteTip))
                throw new ProviderReplacementRefusedException("The recorded repository branch or commit is invalid.");
        }
        if (paths.Any(a => paths.Any(b => a != b && a.StartsWith(b + "/", StringComparison.Ordinal))))
            throw new ProviderReplacementRefusedException("Nested configured repositories cannot be restored.");
    }

    private static void ValidateOmissions(RepositoryOmissions omissions)
    {
        if (omissions.Count < 0 || omissions.Omitted < 0 || omissions.Names.Count > 200
            || omissions.Count != omissions.Omitted + omissions.Names.Count
            || omissions.Names.Sum(name => System.Text.Encoding.UTF8.GetByteCount(name)) > 16384)
            throw new ProviderReplacementRefusedException("The omitted-file summary exceeds its limit.");
    }

    private static bool SafePath(string value) => value.Length is > 0 and <= 1024 && !value.StartsWith('/')
        && !value.Contains('\\') && !value.Any(char.IsControl)
        && value.Split('/').All(part => part.Length > 0 && part is not ("." or ".."));
    private static bool SafeRef(string? value) => value is { Length: > 0 and <= 1024 } && value != "@"
        && !value.StartsWith('-') && !value.StartsWith('/') && !value.EndsWith('/') && !value.EndsWith('.')
        && !value.Contains("..", StringComparison.Ordinal) && !value.Contains("@{", StringComparison.Ordinal)
        && !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || "~^:?*[\\".Contains(c))
        && value.Split('/').All(part => part.Length > 0 && !part.StartsWith('.') && !part.EndsWith(".lock", StringComparison.Ordinal));
    private static bool CommitId(string? value) => value is not null && Regex.IsMatch(value, "\\A(?:[a-f0-9]{40}|[a-f0-9]{64})\\z");
}
