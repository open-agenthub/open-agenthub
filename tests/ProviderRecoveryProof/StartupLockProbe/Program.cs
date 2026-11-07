using AgentHub.Api.Persistence;
using Microsoft.Extensions.Configuration;
using Npgsql;
using System.Diagnostics;

var baseCs = Environment.GetEnvironmentVariable("AGENTHUB_TEST_POSTGRES_CONNECTION_STRING") ?? throw new Exception("Set the local proof database connection.");
var adminBuilder = new NpgsqlConnectionStringBuilder(baseCs) { Database = "postgres", Pooling = false };
if (adminBuilder.Host is not ("127.0.0.1" or "localhost")) throw new Exception("Use a disposable local database.");
var name = "proof529_startup_" + Guid.NewGuid().ToString("N");
await using var admin = new NpgsqlConnection(adminBuilder.ConnectionString); await admin.OpenAsync();
await new NpgsqlCommand("CREATE DATABASE " + name, admin).ExecuteNonQueryAsync();
try {
 var cs = new NpgsqlConnectionStringBuilder(baseCs) { Database = name }.ConnectionString;
 var cfg = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string,string?> { ["ConnectionStrings:Postgres"] = cs }).Build();
 var store = new PostgresSessionStore(cfg); await store.InitializeAsync();
 await using var writer = new NpgsqlConnection(cs); await writer.OpenAsync();
 await using var transaction = await writer.BeginTransactionAsync();
 await new NpgsqlCommand("SELECT id FROM sessions FOR UPDATE", writer, transaction).ExecuteNonQueryAsync();
 var timer = Stopwatch.StartNew(); var startup = store.InitializeAsync();
 await using var observer = new NpgsqlConnection(cs); await observer.OpenAsync();
 for (var attempt=0;attempt<100;attempt++) {
  var waiting = await new NpgsqlCommand("SELECT EXISTS(SELECT 1 FROM pg_locks WHERE relation='sessions'::regclass AND NOT granted)",observer).ExecuteScalarAsync();
  if (waiting is true) break; if(attempt==99)throw new Exception("No real startup DDL wait observed."); await Task.Delay(50);
 }
 Console.WriteLine("Observed actual InitializeAsync waiting for a relation lock behind a session row-lock transaction.");
 try { var read = new NpgsqlCommand("SELECT count(*) FROM sessions",observer) { CommandTimeout=2 }; await read.ExecuteScalarAsync(); throw new Exception("Expected the live reader to queue behind startup DDL."); }
 catch(NpgsqlException error) when(error.InnerException is TimeoutException) { Console.WriteLine("Live session read timed out behind queued startup DDL."); }
 try { await startup; throw new Exception("Expected default startup command timeout."); }
 catch(NpgsqlException error) when(error.InnerException is TimeoutException) { Console.WriteLine($"Actual InitializeAsync failed after {timer.Elapsed.TotalSeconds:F1}s with default command timeout."); }
 await transaction.RollbackAsync(); await store.InitializeAsync();
 Console.WriteLine("PASS: releasing the writer allows actual initialization to finish; no schema or data was changed by the blocked startup.");
} finally { await new NpgsqlCommand("DROP DATABASE "+name+" WITH (FORCE)",admin).ExecuteNonQueryAsync(); }
