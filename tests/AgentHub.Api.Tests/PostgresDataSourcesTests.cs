using AgentHub.Api.Admin;
using AgentHub.Api.Chat;
using AgentHub.Api.Mcp;
using AgentHub.Api.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;
using Xunit;

namespace AgentHub.Api.Tests;

public class PostgresDataSourcesTests
{
    private static IConfiguration Config(string connectionString, string? maxPoolSize = null)
    {
        var values = new Dictionary<string, string?> { ["ConnectionStrings:Postgres"] = connectionString };
        if (maxPoolSize is not null) values[PostgresDataSources.MaxPoolSizeKey] = maxPoolSize;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static NpgsqlConnectionStringBuilder Built(IConfiguration configuration) =>
        new(PostgresDataSources.ConnectionString(configuration, "open-agenthub"));

    [Fact]
    public void PoolSize_DefaultsBelowNpgsqls100()
    {
        var built = Built(Config("Host=db.example.com;Database=agenthub"));

        Assert.Equal(PostgresDataSources.DefaultMaxPoolSize, built.MaxPoolSize);
        Assert.Equal("open-agenthub", built.ApplicationName);
        Assert.Equal("db.example.com", built.Host);
    }

    [Theory]
    [InlineData("Maximum Pool Size=7")]
    [InlineData("maximum pool size=7")]
    [InlineData("MaxPoolSize=7")]
    public void PoolSize_FromConnectionStringIsKept(string setting)
    {
        Assert.Equal(7, Built(Config($"Host=db.example.com;{setting}")).MaxPoolSize);
    }

    [Fact]
    public void PoolSize_ConfigurationWinsOverConnectionString()
    {
        Assert.Equal(3, Built(Config("Host=db.example.com;Maximum Pool Size=7", "3")).MaxPoolSize);
    }

    [Theory]
    [InlineData("0")]
    [InlineData("-1")]
    [InlineData("many")]
    public void PoolSize_InvalidConfigurationFailsAtStartup(string value)
    {
        var error = Assert.Throws<InvalidOperationException>(
            () => PostgresDataSources.ConnectionString(Config("Host=db.example.com", value), "open-agenthub"));
        Assert.Contains(PostgresDataSources.MaxPoolSizeKey, error.Message);
    }

    [Fact]
    public void ApplicationName_FromConnectionStringIsKept()
    {
        Assert.Equal("mine", Built(Config("Host=db.example.com;Application Name=mine")).ApplicationName);
    }

    [Fact]
    public void Stores_ShareOnePool_LocksGetTheirOwn()
    {
        var configuration = Config($"Host=db.example.com;Database=pool_{Guid.NewGuid():N}");

        var shared = PostgresDataSources.Shared(configuration);

        Assert.Same(shared, PostgresDataSources.Shared(Config(configuration.GetConnectionString("Postgres")!)));
        Assert.NotSame(shared, PostgresDataSources.Locks(configuration));
        Assert.Same(PostgresDataSources.Locks(configuration), PostgresDataSources.Locks(configuration));
    }

    [PostgreSqlFact]
    public async Task Stores_InitializedOneAfterAnother_LeaveOneIdleConnection()
    {
        var baseConnectionString = Environment.GetEnvironmentVariable(PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)!;
        var schema = "pool_" + Guid.NewGuid().ToString("N")[..12];
        // A name of its own, so the count below sees this test's pool and nothing the parallel
        // suite has open.
        var connectionString = new NpgsqlConnectionStringBuilder(baseConnectionString)
        {
            SearchPath = schema,
            ApplicationName = schema
        }.ConnectionString;
        await using var admin = NpgsqlDataSource.Create(new NpgsqlConnectionStringBuilder(baseConnectionString) { Pooling = false }.ConnectionString);
        await using (var create = admin.CreateCommand($"CREATE SCHEMA {schema}")) await create.ExecuteNonQueryAsync();
        try
        {
            var configuration = Config(connectionString);
            await new UserDirectory(configuration).InitializeAsync();
            await new ChatLinkCodeStore(configuration).InitializeAsync();
            await new ApiTokenStore(configuration).InitializeAsync();
            await new McpClientStore(configuration).InitializeAsync();
            await new UserRoleStore(configuration).InitializeAsync();
            await new PostgresSessionStore(configuration).InitializeAsync();

            await using var count = admin.CreateCommand(
                "SELECT count(*) FROM pg_stat_activity WHERE application_name = @name");
            count.Parameters.AddWithValue("name", schema);
            // One pool per store held six here.
            Assert.Equal(1L, await count.ExecuteScalarAsync());
        }
        finally
        {
            await using var drop = admin.CreateCommand($"DROP SCHEMA {schema} CASCADE");
            await drop.ExecuteNonQueryAsync();
        }
    }
}
