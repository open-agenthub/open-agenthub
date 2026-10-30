using TelegramSessionNamesProof;
using Xunit;

namespace AgentHub.Api.Tests;

public class TelegramSessionPathsTests
{
    public sealed class DatabaseTheoryAttribute : TheoryAttribute
    {
        public DatabaseTheoryAttribute()
        {
            if (string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)))
                Skip = "Set AGENTHUB_TEST_POSTGRES_CONNECTION_STRING to run the recorded Bot API paths.";
        }
    }

    public static IEnumerable<object[]> Paths => TelegramSessionScenario.Paths.Select(path => new object[] { path });

    [DatabaseTheory]
    [MemberData(nameof(Paths))]
    public async Task OutgoingSessionPathHasItsNameOnTheWire(string path)
        => await TelegramSessionScenario.RunAsync(path,
            Environment.GetEnvironmentVariable(PostgreSqlFactAttribute.ConnectionStringEnvironmentVariable)!);
}
