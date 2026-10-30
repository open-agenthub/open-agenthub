using TelegramSessionNamesProof;
using Xunit;

namespace AgentHub.Api.Tests;

public class TelegramSessionPathsTests
{
    [PostgreSqlFact]
    public async Task EveryOutgoingSessionPathHasItsNameOnTheWire()
    {
        var cs = Environment.GetEnvironmentVariable("AGENTHUB_TEST_POSTGRES_CONNECTION_STRING")!;
        foreach (var path in TelegramSessionScenario.Paths)
            await TelegramSessionScenario.RunAsync(path, cs);
    }
}
