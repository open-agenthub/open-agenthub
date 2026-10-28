using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

public class ClaudeStateCleanupTests
{
    [Theory]
    [InlineData(TarEntryFormat.Gnu)]
    [InlineData(TarEntryFormat.Pax)]
    [InlineData(TarEntryFormat.Ustar)]
    public async Task RemovesOnlyBackupSubtreeAndKeepsMetadata(TarEntryFormat format)
    {
        using var archive = new MemoryStream();
        var modified = DateTimeOffset.FromUnixTimeSeconds(1_700_000_000);
        using (var gzip = new GZipStream(archive, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new TarWriter(gzip, format, leaveOpen: true))
        {
            foreach (var name in new[] { ".claude/backups/config.json", "./.claude/backups/older.json", ".claude/backups-old/config.json", ".claude/projects/history.jsonl" })
            {
                var entry = Entry(format, TarEntryType.RegularFile, name);
                entry.DataStream = new MemoryStream(Encoding.UTF8.GetBytes(name));
                entry.Mode = UnixFileMode.UserRead | UnixFileMode.UserWrite;
                entry.Uid = 1000; entry.Gid = 1001; entry.ModificationTime = modified;
                writer.WriteEntry(entry);
            }
            var link = Entry(format, TarEntryType.SymbolicLink, ".claude/history-link");
            link.LinkName = "projects/history.jsonl";
            writer.WriteEntry(link);
        }
        archive.Position = 0;
        using var cleaned = await ClaudeStateCleanup.RemoveBackupsAsync(archive, default)
            ?? throw new Xunit.Sdk.XunitException("Expected backup removal.");
        using var decompressed = new GZipStream(cleaned, CompressionMode.Decompress, leaveOpen: true);
        using var reader = new TarReader(decompressed);
        var names = new List<string>();
        TarEntry? current;
        while ((current = reader.GetNextEntry()) is not null)
        {
            names.Add(current.Name);
            if (current.EntryType == TarEntryType.SymbolicLink) Assert.Equal("projects/history.jsonl", current.LinkName);
            else
            {
                Assert.Equal(1000, current.Uid); Assert.Equal(1001, current.Gid);
                Assert.Equal(modified, current.ModificationTime);
                Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, current.Mode);
                using var data = new StreamReader(current.DataStream ?? throw new Xunit.Sdk.XunitException("Missing retained data."));
                Assert.Equal(current.Name, await data.ReadToEndAsync());
            }
        }
        Assert.Equal([".claude/backups-old/config.json", ".claude/projects/history.jsonl", ".claude/history-link"], names);
        cleaned.Position = 0;
        Assert.Null(await ClaudeStateCleanup.RemoveBackupsAsync(cleaned, default));
    }

    [Fact]
    public async Task CorruptGzipFailsWithoutProducingReplacement()
    {
        using var corrupt = new MemoryStream([1, 2, 3]);
        await Assert.ThrowsAsync<InvalidDataException>(() => ClaudeStateCleanup.RemoveBackupsAsync(corrupt, default));
    }

    private static TarEntry Entry(TarEntryFormat format, TarEntryType type, string name) => format switch
    {
        TarEntryFormat.Gnu => new GnuTarEntry(type, name),
        TarEntryFormat.Pax => new PaxTarEntry(type, name),
        TarEntryFormat.Ustar => new UstarTarEntry(type, name),
        _ => throw new ArgumentOutOfRangeException(nameof(format))
    };
}
