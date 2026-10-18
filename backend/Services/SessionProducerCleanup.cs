using System.Net;
using k8s;
using k8s.Models;

namespace AgentHub.Api.Services;

public sealed class SessionProducerCleanup(Kubernetes cluster, string ns,
    TimeSpan? timeout = null, TimeSpan? pollInterval = null)
{
    public async Task DeleteAsync(string id, CancellationToken ct)
    {
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
        deadline.CancelAfter(timeout ?? TimeSpan.FromMinutes(2));
        var token = deadline.Token;
        var name = $"session-{id}";
        try
        {
            // Foreground deletion prevents scheduling successors, but returns before dependents
            // exit. A final state upload on SIGTERM must finish before storage is swept.
            await IgnoreMissingAsync(() => cluster.BatchV1.DeleteNamespacedCronJobAsync(name, ns,
                propagationPolicy: "Foreground", cancellationToken: token));
            while (true)
            {
                V1CronJob? cron = null;
                try { cron = await cluster.BatchV1.ReadNamespacedCronJobAsync(name, ns, cancellationToken: token); }
                catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) { }
                var jobs = (await cluster.BatchV1.ListNamespacedJobAsync(ns, cancellationToken: token)).Items
                    .Where(job => job.Metadata.OwnerReferences?.Any(reference => reference.Kind == "CronJob" && reference.Name == name) == true)
                    .ToArray();
                var pods = (await cluster.CoreV1.ListNamespacedPodAsync(ns,
                    labelSelector: $"agenthub.dev/session={id}", cancellationToken: token)).Items;
                // The named lookup also covers old pods that did not carry the session label.
                V1Pod? named = null;
                try { named = await cluster.CoreV1.ReadNamespacedPodAsync(name, ns, cancellationToken: token); }
                catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) { }
                if (cron is null && jobs.Length == 0 && pods.Count == 0 && named is null) return;
                foreach (var job in jobs)
                    await IgnoreMissingAsync(() => cluster.BatchV1.DeleteNamespacedJobAsync(job.Metadata.Name, ns,
                        propagationPolicy: "Foreground", cancellationToken: token));
                foreach (var podName in pods.Select(pod => pod.Metadata.Name).Concat(named is null ? [] : new[] { name }).Distinct())
                    await IgnoreMissingAsync(() => cluster.CoreV1.DeleteNamespacedPodAsync(podName, ns, cancellationToken: token));
                await Task.Delay(pollInterval ?? TimeSpan.FromMilliseconds(500), token);
            }
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            throw new TimeoutException("Session producers are still terminating; retry deletion after they stop.");
        }
    }

    private static async Task IgnoreMissingAsync(Func<Task> delete)
    {
        try { await delete(); }
        catch (k8s.Autorest.HttpOperationException ex) when (ex.Response.StatusCode == HttpStatusCode.NotFound) { }
    }
}
