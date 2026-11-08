using System.Net;
using System.Net.Sockets;

namespace AgentHub.TestSupport;

/// <summary>
/// Starts an <see cref="HttpListener"/> on a free loopback port.
/// </summary>
/// <remarks>
/// HttpListener cannot bind port 0, so a port has to be chosen first. Probing one with
/// TcpListener(0) and releasing it leaves a window in which a parallel test can bind the same
/// port, and Start() then fails with "Address already in use" (#545). The bind itself is atomic,
/// so losing that race is retried on a fresh port instead of failing the test.
/// </remarks>
public static class LoopbackHttpListener
{
    private const int Attempts = 20;

    /// <param name="prefixes">The listener prefixes for a candidate port; every one must bind.</param>
    public static HttpListener Start(Func<int, IEnumerable<string>> prefixes, out int port)
    {
        for (var attempt = 1; ; attempt++)
        {
            port = ProbePort();
            var listener = new HttpListener();
            foreach (var prefix in prefixes(port)) listener.Prefixes.Add(prefix);
            try
            {
                listener.Start();
                return listener;
            }
            catch (HttpListenerException) when (attempt < Attempts)
            {
                // A listener whose Start() failed cannot be started again with other prefixes.
                listener.Close();
            }
        }
    }

    public static HttpListener Start(out int port)
        => Start(p => [$"http://127.0.0.1:{p}/"], out port);

    private static int ProbePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }
}
