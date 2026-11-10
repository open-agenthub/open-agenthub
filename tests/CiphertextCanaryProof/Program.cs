using AgentHub.Api.Tests;

if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable("AGENTHUB_TEST_POSTGRES_CONNECTION_STRING")))
    throw new InvalidOperationException("Set a connection string for real Postgres.");

// Invoke the actual test method so repetitions retain its assertions and schema cleanup,
// without paying test-host startup costs for each new randomized encryption operation.
for (var run = 1; run <= 200; run++)
{
    await new LibraryStoresPostgresTests().McpServerStore_PersistsKindAndSecretJson();
    Console.WriteLine($"Completed {run}/200");
}
