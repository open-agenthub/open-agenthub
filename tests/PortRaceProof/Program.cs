using System.Net;
using System.Net.Sockets;
using AgentHub.TestSupport;

// Usage: dotnet run -- <old|new> [threads] [seconds]
// Every thread starts and stops an HttpListener stand-in in a loop, the way the parallel
// backend suite does. "old" probes a port with TcpListener(0), releases it and binds it;
// "new" uses LoopbackHttpListener. Each thread's probe also competes with the others,
// which is the port thief the CI failure had.
var mode = args.ElementAtOrDefault(0) ?? "old";
var threads = int.Parse(args.ElementAtOrDefault(1) ?? "32");
var seconds = int.Parse(args.ElementAtOrDefault(2) ?? "20");
var until = DateTime.UtcNow.AddSeconds(seconds);
long starts = 0, failures = 0;
string? first = null;

HttpListener Old()
{
    using var probe = new TcpListener(IPAddress.Loopback, 0);
    probe.Start();
    var port = ((IPEndPoint)probe.LocalEndpoint).Port;
    probe.Stop();
    var listener = new HttpListener();
    listener.Prefixes.Add($"http://127.0.0.1:{port}/");
    listener.Start();
    return listener;
}

// Holds ephemeral ports briefly, like parallel tests that keep a TcpListener(0) open.
void Thief()
{
    while (DateTime.UtcNow < until)
    {
        using var held = new TcpListener(IPAddress.Loopback, 0);
        held.Start();
        Thread.SpinWait(2000);
    }
}

var workers = Enumerable.Range(0, threads).Select(i => new Thread(() =>
{
    if (i % 2 == 1) { Thief(); return; }
    while (DateTime.UtcNow < until)
    {
        try
        {
            using var listener = mode == "old" ? Old() : LoopbackHttpListener.Start(out _);
            Interlocked.Increment(ref starts);
        }
        catch (Exception e)
        {
            Interlocked.Increment(ref failures);
            Interlocked.CompareExchange(ref first, e.GetType().Name + ": " + e.Message, null);
        }
    }
})).ToList();
workers.ForEach(t => t.Start());
workers.ForEach(t => t.Join());
Console.WriteLine($"mode={mode} threads={threads} seconds={seconds} starts={starts} failures={failures}{(first is null ? "" : " first=" + first)}");
return failures == 0 ? 0 : 1;
