using System.Security.Cryptography;
using AgentHub.Api.Library;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.DataProtection;

namespace AgentHub.Api.DataProtection;

public static class LegacySecretMigration
{
    public static async Task RunAsync(IServiceProvider services, IConfiguration configuration,
        IWebHostEnvironment environment, ILogger logger)
    {
        var path = configuration["DataProtection:LegacyKeysDirectory"]
            ?? Path.Join(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".aspnet", "DataProtection-Keys");
        // Read old keys only when they survive the upgrade. Never create another local ring.
        var legacy = Directory.Exists(path)
            ? DataProtectionProvider.Create(new DirectoryInfo(path), options => options
                .SetApplicationName(configuration["DataProtection:LegacyApplicationName"]
                    ?? Path.TrimEndingDirectorySeparator(environment.ContentRootPath) + Path.DirectorySeparatorChar)
                .DisableAutomaticKeyGeneration())
            : null;
        var current = services.GetRequiredService<IDataProtectionProvider>();
        var db = PostgresDataSources.Shared(configuration);
        foreach (var (table, column, purpose) in new[]
        {
            ("mcp_servers", "secret_json", McpSecretProtector.Purpose),
            ("webhook_triggers", "secret", "agenthub.webhook-trigger-secret.v1"),
            ("ephemeral_api_mcp", "secret_json", McpSecretProtector.Purpose)
        })
        {
            var unreadable = 0;
            var migrated = 0;
            var rows = new List<(string Id, string Payload)>();
            await using (var command = db.CreateCommand($"SELECT ctid::text, {column} FROM {table} WHERE {column} IS NOT NULL"))
            await using (var reader = await command.ExecuteReaderAsync())
                while (await reader.ReadAsync()) rows.Add((reader.GetString(0), reader.GetString(1)));
            var protector = current.CreateProtector(purpose);
            foreach (var (id, payload) in rows)
            {
                try { protector.Unprotect(payload); continue; }
                catch (CryptographicException) { }
                string plaintext;
                try
                {
                    if (legacy is null) { unreadable++; continue; }
                    plaintext = legacy.CreateProtector(purpose).Unprotect(payload);
                }
                catch (CryptographicException) { unreadable++; continue; }
                // Compare the old payload so an operator's concurrent repair is never overwritten.
                await using var update = db.CreateCommand(
                    $"UPDATE {table} SET {column} = @new WHERE ctid = CAST(@id AS tid) AND {column} = @old");
                update.Parameters.AddWithValue("new", protector.Protect(plaintext));
                update.Parameters.AddWithValue("id", id);
                update.Parameters.AddWithValue("old", payload);
                migrated += await update.ExecuteNonQueryAsync();
            }
            if (migrated > 0) logger.LogInformation("Migrated {Count} readable stored secrets in {Table} to the shared key ring", migrated, table);
            if (unreadable > 0) logger.LogWarning(
                "{Count} stored secrets in {Table} need re-entry. Restore the original keys or re-enter MCP secrets / recreate webhook triggers. Stored values have been retained.", unreadable, table);
        }
    }
}
