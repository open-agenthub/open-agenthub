using Xunit;

namespace AgentHub.Api.Tests;

[Collection(nameof(StartupConcurrencyPostgresTests))]
public sealed class SharedDataProtectionPostgresTests
{
    [PostgreSqlFact]
    public Task Real_replicas_restart_replacement_and_secret_recovery() => DataProtectionProof.Scenario.RunAsync(
        Environment.GetEnvironmentVariable(PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)!,
        Path.Join(AppContext.BaseDirectory, "AgentHub.Api.dll"));
}
