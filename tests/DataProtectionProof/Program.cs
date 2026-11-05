await DataProtectionProof.Scenario.RunAsync(
    Environment.GetEnvironmentVariable("AGENTHUB_TEST_POSTGRES_CONNECTION_STRING")
        ?? throw new InvalidOperationException("Set AGENTHUB_TEST_POSTGRES_CONNECTION_STRING"),
    args.Length > 0 ? Path.GetFullPath(args[0]) : Path.Join(AppContext.BaseDirectory, "AgentHub.Api.dll"));
