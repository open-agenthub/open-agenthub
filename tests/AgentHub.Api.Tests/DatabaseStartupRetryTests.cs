using System.Net;
using System.Net.Sockets;
using System.Text;
using AgentHub.Api.Persistence;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

public sealed class DatabaseStartupRetryTests
{
    [Theory]
    [InlineData("57P03", true)]
    [InlineData("28P01", false)]
    public async Task Recovery_is_retried_but_invalid_credentials_fail_immediately(string state, bool retry)
    {
        // A real protocol ErrorResponse verifies Npgsql's startup exception shape, rather than
        // constructing the exception that the implementation is expected to catch.
        using var server = new TcpListener(IPAddress.Loopback, 0);
        server.Start();
        var port = ((IPEndPoint)server.LocalEndpoint).Port;
        using var stop = new CancellationTokenSource();
        var attempts = 0;
        var serve = Task.Run(async () =>
        {
            try
            {
                while (!stop.IsCancellationRequested)
                {
                    using var client = await server.AcceptTcpClientAsync(stop.Token);
                    Interlocked.Increment(ref attempts);
                    var stream = client.GetStream();
                    var length = new byte[4];
                    await stream.ReadExactlyAsync(length, stop.Token);
                    var startup = new byte[IPAddress.NetworkToHostOrder(BitConverter.ToInt32(length)) - 4];
                    await stream.ReadExactlyAsync(startup, stop.Token);
                    var fields = Encoding.UTF8.GetBytes($"SFATAL\0VFATAL\0C{state}\0Mproof startup response\0\0");
                    await stream.WriteAsync(new byte[] { (byte)'E' }, stop.Token);
                    await stream.WriteAsync(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(fields.Length + 4)), stop.Token);
                    await stream.WriteAsync(fields, stop.Token);
                }
            }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        });
        try
        {
            var config = Config(port);
            if (retry)
                await Assert.ThrowsAsync<TimeoutException>(() => SchemaMigrationLock.RunAsync(config,
                    NullLogger.Instance, () => throw new Exception("must not migrate"), TimeSpan.FromSeconds(2)));
            else
            {
                var error = await Assert.ThrowsAsync<PostgresException>(() => SchemaMigrationLock.RunAsync(config,
                    NullLogger.Instance, () => throw new Exception("must not migrate"), TimeSpan.FromSeconds(2)));
                Assert.Equal(state, error.SqlState);
            }
            Assert.InRange(attempts, retry ? 2 : 1, retry ? 5 : 1);
        }
        finally
        {
            stop.Cancel();
            await serve;
        }
    }

    [Fact]
    public async Task Refused_connection_waits_until_budget_expires()
    {
        using var reserved = new TcpListener(IPAddress.Loopback, 0);
        reserved.Start();
        var config = Config(((IPEndPoint)reserved.LocalEndpoint).Port);
        reserved.Stop();
        var error = await Assert.ThrowsAsync<TimeoutException>(() => SchemaMigrationLock.RunAsync(config,
            NullLogger.Instance, () => throw new Exception("must not migrate"), TimeSpan.FromSeconds(1)));
        Assert.Contains("database to accept connections", error.Message);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved_during_recovery()
    {
        using var reserved = new TcpListener(IPAddress.Loopback, 0);
        reserved.Start();
        var config = Config(((IPEndPoint)reserved.LocalEndpoint).Port);
        reserved.Stop();
        using var stop = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => SchemaMigrationLock.RunAsync(config,
            NullLogger.Instance, () => Task.CompletedTask, ct: stop.Token));
    }

    [PostgreSqlFact]
    public async Task Refusal_then_recovery_then_ready_database_runs_the_migration_once()
    {
        var upstream = new NpgsqlConnectionStringBuilder(Environment.GetEnvironmentVariable(
            PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)!);
        using var reservation = new TcpListener(IPAddress.Loopback, 0);
        reservation.Start();
        var port = ((IPEndPoint)reservation.LocalEndpoint).Port;
        reservation.Stop();
        using var server = new TcpListener(IPAddress.Loopback, port);
        var proxied = new NpgsqlConnectionStringBuilder(upstream.ConnectionString)
        {
            Host = "127.0.0.1", Port = port, Pooling = false,
            SslMode = SslMode.Disable, GssEncryptionMode = GssEncryptionMode.Disable
        };
        var config = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = proxied.ConnectionString
        }).Build();
        var migrations = 0;
        var run = SchemaMigrationLock.RunAsync(config, NullLogger.Instance, async () =>
        {
            await using var conn = new NpgsqlConnection(upstream.ConnectionString);
            await conn.OpenAsync();
            await using var query = new NpgsqlCommand("SELECT 1", conn);
            Assert.Equal(1, await query.ExecuteScalarAsync());
            migrations++;
        }, TimeSpan.FromSeconds(15));
        // The first real socket connection is refused; subsequent attempts see recovery, then
        // the real database, including advisory-lock acquisition and release on that session.
        await Task.Delay(500);
        server.Start();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var serve = Task.Run(async () =>
        {
            for (var attempt = 0; attempt < 3; attempt++)
            {
                using var client = await server.AcceptTcpClientAsync(stop.Token);
                if (attempt < 2)
                {
                    var stream = client.GetStream();
                    var length = new byte[4];
                    await stream.ReadExactlyAsync(length, stop.Token);
                    var startup = new byte[IPAddress.NetworkToHostOrder(BitConverter.ToInt32(length)) - 4];
                    await stream.ReadExactlyAsync(startup, stop.Token);
                    var fields = Encoding.UTF8.GetBytes("SFATAL\0VFATAL\0C57P03\0Mdatabase is in recovery\0\0");
                    await stream.WriteAsync(new byte[] { (byte)'E' }, stop.Token);
                    await stream.WriteAsync(BitConverter.GetBytes(IPAddress.HostToNetworkOrder(fields.Length + 4)), stop.Token);
                    await stream.WriteAsync(fields, stop.Token);
                }
                else
                {
                    using var remote = new TcpClient();
                    await remote.ConnectAsync(upstream.Host!, upstream.Port, stop.Token);
                    var outgoing = client.GetStream().CopyToAsync(remote.GetStream(), stop.Token);
                    var incoming = remote.GetStream().CopyToAsync(client.GetStream(), stop.Token);
                    await Task.WhenAny(outgoing, incoming);
                    stop.Cancel();
                    try { await Task.WhenAll(outgoing, incoming); }
                    catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                }
            }
        });
        try
        {
            await run;
            Assert.Equal(1, migrations);
        }
        finally
        {
            stop.Cancel();
            try { await serve; }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
    }

    private static IConfiguration Config(int port) => new ConfigurationBuilder().AddInMemoryCollection(
        new Dictionary<string, string?>
        {
            ["ConnectionStrings:Postgres"] = $"Host=127.0.0.1;Port={port};Username=proof;Database=proof;SSL Mode=Disable;GSS Encryption Mode=Disable;Timeout=1"
        }).Build();
}
