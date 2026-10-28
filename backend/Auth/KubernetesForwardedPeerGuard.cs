using System.Net;
using AgentHub.Api.Browser;
using k8s;
using k8s.Models;

namespace AgentHub.Api.Auth;

public interface IForwardedPeerGuard
{
    Task<bool> CanForwardAsync(IPAddress address, CancellationToken ct);
}

public sealed class KubernetesForwardedPeerGuard : BackgroundService, IForwardedPeerGuard
{
    private readonly Kubernetes _kubernetes;
    private readonly string _namespace;
    private readonly ILogger<KubernetesForwardedPeerGuard> _logger;
    private bool _outageReported;
    private readonly object _gate = new();
    private Dictionary<string, IPAddress[]>? _pods;
    private DateTimeOffset _refreshed;
    private const string Selector = "agenthub.dev/component in (agent,browser)";

    public KubernetesForwardedPeerGuard(IConfiguration configuration, ILogger<KubernetesForwardedPeerGuard> logger)
    {
        _logger = logger;
        _namespace = configuration["AgentHub:Namespace"] ?? "agenthub-sessions";
        _kubernetes = new Kubernetes(KubernetesClientConfiguration.IsInCluster()
            ? KubernetesClientConfiguration.InClusterConfig()
            : KubernetesClientConfiguration.BuildConfigFromConfigFile());
    }

    public Task<bool> CanForwardAsync(IPAddress address, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_pods is null || DateTimeOffset.UtcNow - _refreshed >= TimeSpan.FromSeconds(5))
            {
                Invalidate();
                return Task.FromResult(false);
            }
            return Task.FromResult(!_pods.Values.SelectMany(ips => ips)
                .Any(ip => IpAddressNormalization.Equals(address, ip)));
        }
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                using var timeout = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
                timeout.CancelAfter(TimeSpan.FromSeconds(4));
                var pods = await _kubernetes.CoreV1.ListNamespacedPodAsync(_namespace,
                    labelSelector: Selector, cancellationToken: timeout.Token);
                // Watch from the list's version so a pod assigned between list and watch is not
                // missed. Refresh periodically to detect a stalled stream; no request does I/O.
                var response = _kubernetes.CoreV1.ListNamespacedPodWithHttpMessagesAsync(_namespace,
                    labelSelector: Selector, resourceVersion: pods.Metadata.ResourceVersion,
                    watch: true, timeoutSeconds: 2, cancellationToken: timeout.Token);
                lock (_gate)
                {
                    _pods = pods.Items.ToDictionary(pod => pod.Metadata.Name, Addresses);
                    _refreshed = DateTimeOffset.UtcNow;
                    if (_outageReported)
                        _logger.LogInformation("Forwarded-header workload inventory recovered; configured proxy trust is restored.");
                    _outageReported = false;
                }
                await foreach (var (type, pod) in response.WatchAsync<V1Pod, V1PodList>(cancellationToken: timeout.Token))
                {
                    lock (_gate)
                    {
                        if (type == WatchEventType.Deleted) _pods.Remove(pod.Metadata.Name);
                        else if (type is WatchEventType.Added or WatchEventType.Modified)
                            _pods[pod.Metadata.Name] = Addresses(pod);
                    }
                }
            }
            catch (k8s.Autorest.HttpOperationException) { Invalidate(); }
            catch (HttpRequestException) { Invalidate(); }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            catch (OperationCanceledException) { Invalidate(); }
            catch (k8s.KubernetesException) { Invalidate(); }
            catch (IOException) { Invalidate(); }
            // An unavailable or stale inventory must not turn workloads into trusted proxies.
            if (stoppingToken.IsCancellationRequested) break;
            await Task.Delay(TimeSpan.FromMilliseconds(250), stoppingToken);
        }
    }

    private static IPAddress[] Addresses(V1Pod pod)
        => new[] { pod.Status?.PodIP }.Concat(pod.Status?.PodIPs?.Select(ip => ip.Ip) ?? [])
            .Select(value => IPAddress.TryParse(value, out var ip) ? ip : null)
            .OfType<IPAddress>().ToArray();

    private void Invalidate()
    {
        lock (_gate)
        {
            _pods = null;
            if (_outageReported) return;
            _outageReported = true;
            _logger.LogWarning("Forwarded-header workload inventory unavailable; using socket addresses for API-key throttling. Clients sharing a proxy may temporarily share a throttle bucket.");
        }
    }

    public override void Dispose()
    {
        base.Dispose();
        _kubernetes.Dispose();
    }
}
