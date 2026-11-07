using System.Xml.Linq;
using AgentHub.Api.Persistence;
using Microsoft.AspNetCore.DataProtection.Repositories;
using Npgsql;

namespace AgentHub.Api.DataProtection;

public sealed class PostgresKeyRepository(IConfiguration configuration) : IXmlRepository
{
    private readonly NpgsqlDataSource _db = PostgresDataSources.Shared(configuration);

    public async Task InitializeAsync()
    {
        await SchemaScript.ApplyAsync(_db, nameof(PostgresKeyRepository), """
            CREATE TABLE IF NOT EXISTS data_protection_keys (
                id BIGINT GENERATED ALWAYS AS IDENTITY PRIMARY KEY,
                friendly_name TEXT NOT NULL,
                xml TEXT NOT NULL
            )
            """, CancellationToken.None);
    }

    public IReadOnlyCollection<XElement> GetAllElements()
    {
        using var command = _db.CreateCommand("SELECT xml FROM data_protection_keys ORDER BY id");
        using var reader = command.ExecuteReader();
        var elements = new List<XElement>();
        while (reader.Read()) elements.Add(XElement.Parse(reader.GetString(0)));
        return elements;
    }

    public void StoreElement(XElement element, string friendlyName)
    {
        // Keep revocations as well as keys: dropping either breaks the framework's key lifecycle.
        using var command = _db.CreateCommand(
            "INSERT INTO data_protection_keys (friendly_name, xml) VALUES (@name, @xml)");
        command.Parameters.AddWithValue("name", friendlyName);
        command.Parameters.AddWithValue("xml", element.ToString(SaveOptions.DisableFormatting));
        command.ExecuteNonQuery();
    }
}
