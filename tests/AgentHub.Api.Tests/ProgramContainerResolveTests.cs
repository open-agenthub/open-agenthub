using System.Diagnostics;
using System.Reflection;
using k8s;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// Builds the real container from Program.cs and resolves every singleton and hosted service
/// (#371). A dependency cycle through a factory registration (<c>sp => sp.GetRequiredService&lt;X&gt;()</c>)
/// is invisible to ValidateOnBuild and to the container's own cycle check; resolving it deadlocks
/// on the container's singleton lock instead of throwing. Only resolving, with a timeout, finds it.
/// </summary>
public sealed class ProgramContainerResolveTests
{
    private static readonly TimeSpan PerService = TimeSpan.FromSeconds(10);
    private static readonly AsyncLocal<bool> Capturing = new();

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Every_singleton_and_hosted_service_resolves(bool chatEnabled)
    {
        var args = new List<string>
        {
            "--ConnectionStrings:Postgres=Host=127.0.0.1;Port=1;Username=unused;Database=unused",
            "--Auth:ApiKey=" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24)),
            "--AgentHub:Namespace=agenthub-sessions",
            "--Browser:Enabled=false",
            "--environment=Production"
        };
        if (chatEnabled)
            args.AddRange([
                "--Chat:Telegram:Enabled=true", "--Chat:Telegram:BotToken=0:unused",
                "--Chat:Signal:Enabled=true", "--Chat:Signal:ApiUrl=http://127.0.0.1:1", "--Chat:Signal:Number=+10000000000"
            ]);

        using var kubeconfig = KubeconfigIfMissing.Create();
        var (services, provider) = BuildProgramContainer(args.ToArray());

        var failures = new List<string>();
        var types = services
            .Where(d => d.Lifetime == ServiceLifetime.Singleton && !d.ServiceType.IsGenericTypeDefinition)
            .Select(d => d.ServiceType)
            .Append(typeof(IEnumerable<IHostedService>))
            .Distinct()
            .ToList();
        foreach (var type in types)
        {
            var resolve = Task.Run(() => provider.GetService(type));
            try
            {
                if (!resolve.Wait(PerService))
                {
                    failures.Add($"{type.FullName}: no result after {PerService.TotalSeconds:0}s (dependency cycle through a factory?)");
                    // The deadlocked resolution holds the container's lock; nothing after it can resolve.
                    break;
                }
            }
            catch (AggregateException e) when (e.InnerException is InvalidOperationException { Message: var m } && m.Contains("circular", StringComparison.OrdinalIgnoreCase))
            {
                failures.Add($"{type.FullName}: {m}");
            }
            catch (AggregateException)
            {
                // Some constructors reach for things this test does not provide (a database on a
                // closed port). Their dependencies were resolved before the constructor ran, which
                // is all a cycle needs to show up.
            }
        }

        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    private static (IServiceCollection Services, IServiceProvider Provider) BuildProgramContainer(string[] args)
    {
        IServiceCollection? services = null;
        IServiceProvider? provider = null;
        using var subscription = DiagnosticListener.AllListeners.Subscribe(new ListenerObserver(listener =>
        {
            if (listener.Name != "Microsoft.Extensions.Hosting") return;
            listener.Subscribe(new EventObserver(e =>
            {
                // Other tests build hosts in parallel; only the one Program builds on this flow counts.
                if (!Capturing.Value) return;
                // Raised before the builder applies its configuration, so this callback sees the
                // finished service collection.
                if (e.Key == "HostBuilding" && e.Value is IHostBuilder builder)
                    builder.ConfigureServices(collection => services = collection);
                if (e.Key == "HostBuilt" && e.Value is IHost host)
                {
                    provider = host.Services;
                    // Stop Program right after Build: nothing after it (schema, Run) belongs to this test.
                    throw new StopProgram();
                }
            }));
        }));

        var entry = typeof(AgentHub.Api.Persistence.SchemaMigrationLock).Assembly.EntryPoint!;
        Capturing.Value = true;
        try
        {
            entry.Invoke(null, [args]);
            throw new InvalidOperationException("Program ran to completion without building its host.");
        }
        catch (TargetInvocationException e) when (Unwrap(e) is StopProgram) { }
        finally { Capturing.Value = false; }

        Assert.NotNull(services);
        Assert.NotNull(provider);
        return (services!, provider!);
    }

    private static Exception Unwrap(Exception e)
    {
        while (e is TargetInvocationException or AggregateException && e.InnerException is not null) e = e.InnerException!;
        return e;
    }

    private sealed class StopProgram : Exception;

    private sealed class ListenerObserver(Action<DiagnosticListener> onNext) : IObserver<DiagnosticListener>
    {
        public void OnNext(DiagnosticListener value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    private sealed class EventObserver(Action<KeyValuePair<string, object?>> onNext) : IObserver<KeyValuePair<string, object?>>
    {
        public void OnNext(KeyValuePair<string, object?> value) => onNext(value);
        public void OnError(Exception error) { }
        public void OnCompleted() { }
    }

    /// <summary>
    /// The Kubernetes clients read the default kubeconfig in their constructors. Writes a stand-in
    /// that points nowhere when there is none, and removes only what it wrote; a real one is left alone.
    /// </summary>
    private sealed class KubeconfigIfMissing : IDisposable
    {
        private readonly string? _written;
        private readonly string? _createdDirectory;

        private KubeconfigIfMissing(string? written, string? createdDirectory)
        {
            _written = written;
            _createdDirectory = createdDirectory;
        }

        public static KubeconfigIfMissing Create()
        {
            var path = KubernetesClientConfiguration.KubeConfigDefaultLocation;
            if (File.Exists(path)) return new(null, null);
            var directory = Path.GetDirectoryName(path)!;
            var created = Directory.Exists(directory) ? null : directory;
            Directory.CreateDirectory(directory);
            File.WriteAllText(path, """
                apiVersion: v1
                kind: Config
                clusters: [{name: none, cluster: {server: "http://127.0.0.1:1"}}]
                users: [{name: none, user: {token: unused}}]
                contexts: [{name: none, context: {cluster: none, user: none, namespace: agenthub-sessions}}]
                current-context: none
                """);
            return new(path, created);
        }

        public void Dispose()
        {
            if (_written is not null) File.Delete(_written);
            if (_createdDirectory is not null) Directory.Delete(_createdDirectory);
        }
    }
}
