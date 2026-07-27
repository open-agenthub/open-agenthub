namespace AgentHub.Api.Services;

/// <summary>Pure helpers for parent→descendant session checks (in-pod peer auth).</summary>
public static class SessionDescent
{
    public const int MaxHops = 64;

    /// <summary>
    /// Returns true when <paramref name="candidateId"/> is a strict descendant of
    /// <paramref name="ancestorId"/> by walking parent links via <paramref name="parentOf"/>.
    /// Cycles and chains longer than <see cref="MaxHops"/> yield false.
    /// </summary>
    public static bool IsDescendant(string candidateId, string ancestorId, Func<string, string?> parentOf)
    {
        if (string.IsNullOrEmpty(candidateId) || string.IsNullOrEmpty(ancestorId))
            return false;
        if (candidateId == ancestorId)
            return false;

        var current = candidateId;
        for (var hop = 0; hop < MaxHops; hop++)
        {
            var parent = parentOf(current);
            if (parent is null)
                return false;
            if (parent == ancestorId)
                return true;
            current = parent;
        }
        return false;
    }
}
