using System.Reflection;
using AgentHub.Api.Models;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Settings → Secrets builds "where is this used" (#254) from the session listing's
/// effectiveSecrets and the project listing's secretIds. Neither response type may have anywhere a
/// value could end up: they carry ids, names, delivery and provenance only.
/// </summary>
public class SecretUsageCarriesNoValueTests
{
    private static IEnumerable<string> PropertyPaths(Type type, string prefix = "", int depth = 0)
    {
        foreach (var property in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            var path = prefix + property.Name;
            yield return path;
            var inner = property.PropertyType.IsGenericType
                ? property.PropertyType.GetGenericArguments().FirstOrDefault()
                : property.PropertyType;
            if (depth < 2 && inner?.Namespace == typeof(SessionSecretProvenance).Namespace)
                foreach (var nested in PropertyPaths(inner, path + ".", depth + 1)) yield return nested;
        }
    }

    [Fact]
    public void TheProvenanceAndProjectTypes_HaveNoValueField()
    {
        Assert.Equal(["Id", "Name", "Delivery", "Provenance", "InheritToChildren"],
            typeof(SessionSecretProvenance).GetProperties().Select(p => p.Name));
        Assert.DoesNotContain(PropertyPaths(typeof(ProjectInfo)), p => p.Contains("Value", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void TheSessionListing_CarriesSecretsOnlyAsProvenance()
    {
        var secretPaths = PropertyPaths(typeof(SessionInfo)).Where(p => p.Contains("Secret", StringComparison.OrdinalIgnoreCase)).ToList();
        Assert.Contains("EffectiveSecrets", secretPaths);
        Assert.DoesNotContain(secretPaths, p => p.Contains("Value", StringComparison.OrdinalIgnoreCase));
    }
}
