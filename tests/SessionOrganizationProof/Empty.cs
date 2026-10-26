using AgentHub.Api.Models;
using System.Collections;
using System.Reflection;
public class Empty : DispatchProxy
{
    public static T Of<T>() where T : class => Create<T, Empty>();
    protected override object? Invoke(MethodInfo? m, object?[]? a)
    {
        if (m!.Name == "GetSummariesAsync") return Task.FromResult<IReadOnlyDictionary<string, AgentHub.Api.Browser.BrowserSummary>>(((IReadOnlyCollection<string>)a![0]!).ToDictionary(id => id, _ => AgentHub.Api.Browser.BrowserSummary.Stopped));
        var t = m.ReturnType;
        if (t == typeof(void)) return null;
        if (t == typeof(Task)) return Task.CompletedTask;
        if (t == typeof(ValueTask)) return ValueTask.CompletedTask;
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(Task<>))
            return typeof(Task).GetMethod(nameof(Task.FromResult))!.MakeGenericMethod(t.GenericTypeArguments[0]).Invoke(null, [Default(t.GenericTypeArguments[0])]);
        return Default(t);
    }
    static object? Default(Type t)
    {
        if (t.IsGenericType && t.GetGenericTypeDefinition() == typeof(IReadOnlySet<>)) return Activator.CreateInstance(typeof(HashSet<>).MakeGenericType(t.GenericTypeArguments));
        if (t == typeof(AgentHub.Api.Browser.BrowserSummary)) return AgentHub.Api.Browser.BrowserSummary.Stopped;
        if (t == typeof(string)) return "";
        if (t.IsValueType) return Activator.CreateInstance(t);
        if (t.IsGenericType && typeof(IEnumerable).IsAssignableFrom(t) && !t.IsArray)
        {
            var args = t.GenericTypeArguments;
            if (args.Length == 1) return Array.CreateInstance(args[0], 0);
            if (args.Length == 2) return Activator.CreateInstance(typeof(Dictionary<,>).MakeGenericType(args));
        }
        return null;
    }
}

class AllAgents : AgentHub.Api.Agents.IAllowedAgentsProvider
{
    public Task<IReadOnlyList<AgentKind>> GetAllowedAsync(CancellationToken ct = default) => Task.FromResult<IReadOnlyList<AgentKind>>(Enum.GetValues<AgentKind>());
    public Task<bool> IsAllowedAsync(AgentKind agent, CancellationToken ct = default) => Task.FromResult(true);
}
