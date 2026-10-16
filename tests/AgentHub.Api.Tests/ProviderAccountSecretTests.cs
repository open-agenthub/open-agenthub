using System.Text;
using System.Text.Json;
using AgentHub.Api.Models;
using AgentHub.Api.Services;
using Xunit;

namespace AgentHub.Api.Tests;

/// <summary>
/// The multi-account layout of a provider secret and its lazy migration — docs/provider-accounts.md.
/// Pure data in, pure data out; the Kubernetes write is one Replace of what <c>Write</c> returns.
/// </summary>
public sealed class ProviderAccountSecretTests
{
    private static byte[] File(string token) =>
        Encoding.UTF8.GetBytes($"{{\"claudeAiOauth\":{{\"accessToken\":\"{token}\"}}}}");

    private static ProviderAccountIdentity Identity(string key, string? email = null) => new(key, email, null);

    [Fact]
    public void Read_MigratesTheSingleFileLayoutIntoADefaultAccount_Once()
    {
        var legacy = new Dictionary<string, byte[]> { ["credentials.json"] = File("old") };

        var set = ProviderAccountSecret.Read(legacy, AgentKind.Claude);

        Assert.True(set.Dirty);
        var account = Assert.Single(set.Accounts);
        Assert.Equal(ProviderAccountSecret.LegacyId, account.Id);
        Assert.Equal("Default", account.Label);
        Assert.True(account.IsDefault);
        Assert.Equal(File("old"), set.Files[ProviderAccountSecret.LegacyId]);

        // The migrated layout reads back clean: nothing to write a second time.
        var written = ProviderAccountSecret.Write(set, AgentKind.Claude);
        Assert.False(written.ContainsKey("credentials.json"));
        Assert.True(written.ContainsKey("default.credentials.json"));
        Assert.True(written.ContainsKey(ProviderAccountSecret.IndexKey));
        var again = ProviderAccountSecret.Read(written, AgentKind.Claude);
        Assert.False(again.Dirty);
        Assert.Equal("default", Assert.Single(again.Accounts).Id);
    }

    [Fact]
    public void Read_ReconcilesIndexAndFilesInBothDirections()
    {
        var set = new ProviderAccountSet();
        set.Accounts.Add(new ProviderAccount { Id = "aaaa", Label = "Listed but no file", IsDefault = true });
        set.Accounts.Add(new ProviderAccount { Id = "bbbb", Label = "Fine" });
        set.Files["bbbb"] = File("b");
        var data = ProviderAccountSecret.Write(set, AgentKind.Codex);
        data["cccc.auth.json"] = File("c"); // a file nobody indexed

        var read = ProviderAccountSecret.Read(data, AgentKind.Codex);

        Assert.True(read.Dirty);
        Assert.Equal(["bbbb", "cccc"], read.Accounts.Select(a => a.Id).Order());
        // The default moved off the entry that had no file.
        Assert.Equal("bbbb", read.Default!.Id);
        Assert.Equal("cccc", read.Find("cccc")!.Label);
    }

    [Fact]
    public void Read_ToleratesACorruptIndexByRebuildingItFromTheFiles()
    {
        var data = new Dictionary<string, byte[]>
        {
            [ProviderAccountSecret.IndexKey] = Encoding.UTF8.GetBytes("not json"),
            ["a1b2.auth.json"] = File("x")
        };

        var set = ProviderAccountSecret.Read(data, AgentKind.Cursor);

        Assert.True(set.Dirty);
        Assert.Equal("a1b2", Assert.Single(set.Accounts).Id);
    }

    [Fact]
    public void Read_IgnoresKeysThatAreNotAccountFiles()
    {
        var data = new Dictionary<string, byte[]>
        {
            ["Bad Id.credentials.json"] = File("x"),
            ["unrelated"] = File("y"),
            ["ok01.credentials.json"] = File("z")
        };

        var set = ProviderAccountSecret.Read(data, AgentKind.Claude);

        Assert.Equal("ok01", Assert.Single(set.Accounts).Id);
    }

    [Fact]
    public void HasAnyAccount_SeesBothLayouts()
    {
        Assert.False(ProviderAccountSecret.HasAnyAccount(null, AgentKind.Claude));
        Assert.False(ProviderAccountSecret.HasAnyAccount(new Dictionary<string, byte[]>(), AgentKind.Claude));
        Assert.True(ProviderAccountSecret.HasAnyAccount(new Dictionary<string, byte[]> { ["credentials.json"] = File("x") }, AgentKind.Claude));
        Assert.True(ProviderAccountSecret.HasAnyAccount(new Dictionary<string, byte[]> { ["abcd.credentials.json"] = File("x") }, AgentKind.Claude));
        Assert.False(ProviderAccountSecret.HasAnyAccount(new Dictionary<string, byte[]> { ["abcd.auth.json"] = File("x") }, AgentKind.Claude));
    }

    [Fact]
    public void ResolveId_PrefersTheNamedAccountThenTheDefault()
    {
        var set = new ProviderAccountSet();
        set.Accounts.Add(new ProviderAccount { Id = "one" });
        set.Accounts.Add(new ProviderAccount { Id = "two", IsDefault = true });

        Assert.Equal("one", ProviderAccountSecret.ResolveId(set, "one"));
        Assert.Equal("two", ProviderAccountSecret.ResolveId(set, null));
        Assert.Equal("two", ProviderAccountSecret.ResolveId(set, "missing"));
        Assert.Null(ProviderAccountSecret.ResolveId(new ProviderAccountSet(), null));
    }

    // --- Attach: the rules a pod upload is sorted by -----------------------------------------

    [Fact]
    public void Attach_FirstLoginEverCreatesTheDefaultAccount()
    {
        var set = new ProviderAccountSet();

        var result = ProviderAccountSecret.Attach(set, File("t"), Identity("acc-1", "a@example.com"), mountedId: null);

        Assert.True(result.Created);
        var account = Assert.Single(set.Accounts);
        Assert.Equal(result.AccountId, account.Id);
        Assert.True(account.IsDefault);
        Assert.Equal("a@example.com", account.Label);
        Assert.Equal("acc-1", account.Identity!.Key);
    }

    [Fact]
    public void Attach_RotationOfTheMountedAccountUpdatesItInPlace()
    {
        var set = new ProviderAccountSet();
        var first = ProviderAccountSecret.Attach(set, File("t1"), Identity("acc-1"), null);

        var rotated = ProviderAccountSecret.Attach(set, File("t2"), identity: null, mountedId: first.AccountId);

        Assert.False(rotated.Created);
        Assert.Equal(first.AccountId, rotated.AccountId);
        Assert.Equal(File("t2"), set.Files[first.AccountId]);
        Assert.Single(set.Accounts);
    }

    /// <summary>The case the feature exists for: /login inside a session as somebody else.</summary>
    [Fact]
    public void Attach_ADifferentIdentityOnTheMountedAccountBecomesANewAccount()
    {
        var set = new ProviderAccountSet();
        var first = ProviderAccountSecret.Attach(set, File("t1"), Identity("acc-1", "one@example.com"), null);

        var other = ProviderAccountSecret.Attach(set, File("t2"), Identity("acc-2", "two@example.com"), mountedId: first.AccountId);

        Assert.True(other.Created);
        Assert.NotEqual(first.AccountId, other.AccountId);
        Assert.Equal(2, set.Accounts.Count);
        // The first login kept its token; nothing was overwritten.
        Assert.Equal(File("t1"), set.Files[first.AccountId]);
        Assert.Equal(File("t2"), set.Files[other.AccountId]);
        Assert.True(set.Find(first.AccountId)!.IsDefault);
        Assert.False(set.Find(other.AccountId)!.IsDefault);
    }

    [Fact]
    public void Attach_WithoutAMountJoinsTheAccountWithTheSameIdentity()
    {
        var set = new ProviderAccountSet();
        var first = ProviderAccountSecret.Attach(set, File("t1"), Identity("acc-1"), null);

        var again = ProviderAccountSecret.Attach(set, File("t2"), Identity("acc-1"), mountedId: null);

        Assert.False(again.Created);
        Assert.Equal(first.AccountId, again.AccountId);
        Assert.Equal(File("t2"), set.Files[first.AccountId]);
    }

    [Fact]
    public void Attach_WithoutAMountOrIdentityJoinsAnAccountWithTheSameBytes()
    {
        var set = new ProviderAccountSet();
        var first = ProviderAccountSecret.Attach(set, File("same"), null, null);

        var echoed = ProviderAccountSecret.Attach(set, File("same"), null, null);

        Assert.False(echoed.Created);
        Assert.Equal(first.AccountId, echoed.AccountId);
    }

    [Fact]
    public void Attach_WithoutAMountAndAnUnknownIdentityCreatesANonDefaultAccount()
    {
        var set = new ProviderAccountSet();
        ProviderAccountSecret.Attach(set, File("t1"), Identity("acc-1"), null);

        var created = ProviderAccountSecret.Attach(set, File("t2"), Identity("acc-2"), null);

        Assert.True(created.Created);
        Assert.False(set.Find(created.AccountId)!.IsDefault);
        Assert.Equal(2, set.Accounts.Count);
    }

    [Fact]
    public void Attach_LearnsAnIdentityForAMigratedAccountAndKeepsAUserLabel()
    {
        var legacy = new Dictionary<string, byte[]> { ["credentials.json"] = File("old") };
        var set = ProviderAccountSecret.Read(legacy, AgentKind.Claude);

        ProviderAccountSecret.Attach(set, File("new"), Identity("acc-1", "me@example.com"), mountedId: "default");

        var account = Assert.Single(set.Accounts);
        Assert.Equal("me@example.com", account.Label);
        Assert.Equal("acc-1", account.Identity!.Key);

        account.Label = "Work";
        ProviderAccountSecret.Attach(set, File("newer"), Identity("acc-1", "me@example.com"), mountedId: "default");
        Assert.Equal("Work", account.Label);
    }

    [Fact]
    public void Remove_MovesTheDefaultToTheFirstRemainingAccount()
    {
        var set = new ProviderAccountSet();
        var a = ProviderAccountSecret.Attach(set, File("a"), Identity("a"), null);
        var b = ProviderAccountSecret.Attach(set, File("b"), Identity("b"), null);

        Assert.True(ProviderAccountSecret.Remove(set, a.AccountId));

        Assert.Equal(b.AccountId, Assert.Single(set.Accounts).Id);
        Assert.True(set.Default!.IsDefault);
        Assert.False(set.Files.ContainsKey(a.AccountId));
        Assert.False(ProviderAccountSecret.Remove(set, "missing"));
    }

    [Fact]
    public void MakeDefault_IsExclusive()
    {
        var set = new ProviderAccountSet();
        var a = ProviderAccountSecret.Attach(set, File("a"), Identity("a"), null);
        var b = ProviderAccountSecret.Attach(set, File("b"), Identity("b"), null);

        ProviderAccountSecret.MakeDefault(set, b.AccountId);

        Assert.False(set.Find(a.AccountId)!.IsDefault);
        Assert.True(set.Find(b.AccountId)!.IsDefault);
    }

    [Fact]
    public void Write_NeverPutsTheMatchingKeyOrFilesIntoTheIndexView()
    {
        var set = new ProviderAccountSet();
        var created = ProviderAccountSecret.Attach(set, File("t"), Identity("acc-1", "a@example.com"), null);

        var info = ProviderAccountInfo.From(set.Find(created.AccountId)!);
        var json = JsonSerializer.Serialize(info, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        Assert.DoesNotContain("acc-1", json);
        Assert.DoesNotContain("accessToken", json);
        Assert.Contains("a@example.com", json);
    }

    [Theory]
    [InlineData("", false)]
    [InlineData("default", true)]
    [InlineData("3f9a1c2b7e0d4a61", true)]
    [InlineData("has-dash", false)]
    [InlineData("UPPER", false)]
    [InlineData("../etc", false)]
    public void IsValidId_AcceptsOnlyShortLowercaseAlphanumerics(string id, bool valid)
        => Assert.Equal(valid, ProviderAccountSecret.IsValidId(id));

    // ------------------------------------------------------------------ usage limits (docs/account-limits.md)

    private static readonly DateTime Now = new(2026, 10, 11, 12, 0, 0, DateTimeKind.Utc);

    private static ProviderAccountSet Three()
    {
        var set = new ProviderAccountSet();
        set.Accounts.Add(new ProviderAccount { Id = "work", Label = "Work", IsDefault = true, LastUsedAt = Now.AddHours(-1) });
        set.Accounts.Add(new ProviderAccount { Id = "home", Label = "Home", LastUsedAt = Now.AddDays(-3) });
        set.Accounts.Add(new ProviderAccount { Id = "spare", Label = "Spare" });
        foreach (var account in set.Accounts) set.Files[account.Id] = File(account.Id);
        return set;
    }

    [Fact]
    public void ExhaustedMark_SurvivesTheIndexRoundTrip_AndExpiresOnItsOwn()
    {
        var set = Three();
        Assert.True(ProviderAccountSecret.MarkExhausted(set, "work", Now.AddHours(2), "five_hour 100%"));
        Assert.False(ProviderAccountSecret.MarkExhausted(set, "nope", Now.AddHours(2), null));

        var read = ProviderAccountSecret.Read(ProviderAccountSecret.Write(set, AgentKind.Claude), AgentKind.Claude);

        var work = read.Find("work")!;
        Assert.Equal(Now.AddHours(2), work.ExhaustedUntil);
        Assert.Equal("five_hour 100%", work.ExhaustedReason);
        Assert.True(ProviderAccountSecret.IsExhausted(work, Now));
        // Past the reset nothing has to clear it.
        Assert.False(ProviderAccountSecret.IsExhausted(work, Now.AddHours(3)));
        Assert.Equal(["home", "spare"], read.Available(Now).Select(a => a.Id));
        Assert.Equal(["work", "home", "spare"], read.Available(Now.AddHours(3)).Select(a => a.Id));
    }

    [Fact]
    public void ResolveId_StepsAroundAnExhaustedDefault_ButHonoursAPin()
    {
        var set = Three();
        Assert.Equal("work", ProviderAccountSecret.ResolveId(set, null, Now));

        ProviderAccountSecret.MarkExhausted(set, "work", Now.AddHours(1), null);
        Assert.Equal("home", ProviderAccountSecret.ResolveId(set, null, Now));
        // The pin is a choice: it is mounted exhausted rather than swapped.
        Assert.Equal("work", ProviderAccountSecret.ResolveId(set, "work", Now));
        // Once the window has reset the default is the default again.
        Assert.Equal("work", ProviderAccountSecret.ResolveId(set, null, Now.AddHours(2)));

        // Everything exhausted: the session still has to mount something, so the default it is.
        foreach (var account in set.Accounts) ProviderAccountSecret.MarkExhausted(set, account.Id, Now.AddHours(1), null);
        Assert.Equal("work", ProviderAccountSecret.ResolveId(set, null, Now));
    }

    [Fact]
    public void NextAvailable_PrefersTheDefault_ThenTheLeastRecentlyUsed_NeverTheCurrentOrAnExhaustedOne()
    {
        var set = Three();
        // Switching away from "spare": the default wins over the idle one.
        Assert.Equal("work", ProviderAccountSecret.NextAvailable(set, "spare", Now)!.Id);
        // Switching away from the default: never-used before used-three-days-ago.
        Assert.Equal("spare", ProviderAccountSecret.NextAvailable(set, "work", Now)!.Id);

        ProviderAccountSecret.MarkExhausted(set, "spare", Now.AddHours(1), null);
        Assert.Equal("home", ProviderAccountSecret.NextAvailable(set, "work", Now)!.Id);

        ProviderAccountSecret.MarkExhausted(set, "home", Now.AddHours(1), null);
        Assert.Null(ProviderAccountSecret.NextAvailable(set, "work", Now));
    }

    [Fact]
    public void ClearExhausted_LiftsTheMark_AndIsIdempotent()
    {
        var set = Three();
        ProviderAccountSecret.MarkExhausted(set, "home", Now.AddHours(1), "matched");
        set.Dirty = false;

        Assert.True(ProviderAccountSecret.ClearExhausted(set, "home"));
        Assert.True(set.Dirty);
        Assert.Null(set.Find("home")!.ExhaustedUntil);
        Assert.Null(set.Find("home")!.ExhaustedReason);

        set.Dirty = false;
        Assert.True(ProviderAccountSecret.ClearExhausted(set, "home"));
        Assert.False(set.Dirty);
        Assert.False(ProviderAccountSecret.ClearExhausted(set, "nope"));
    }

    [Fact]
    public void Info_ReportsTheMarkOnlyWhileItApplies()
    {
        var account = new ProviderAccount { Id = "work", Label = "Work", ExhaustedUntil = Now.AddHours(1), ExhaustedReason = "five_hour" };

        var during = ProviderAccountInfo.From(account, Now);
        var after = ProviderAccountInfo.From(account, Now.AddHours(2));

        Assert.True(during.IsExhausted);
        Assert.Equal(Now.AddHours(1), during.ExhaustedUntil);
        Assert.Equal("five_hour", during.ExhaustedReason);
        Assert.False(after.IsExhausted);
        Assert.Null(after.ExhaustedUntil);
        Assert.Null(after.ExhaustedReason);
    }

    [Fact]
    public void NormalizeLabel_TrimsAndBounds()
    {
        Assert.Equal("Work", ProviderAccountSecret.NormalizeLabel("  Work "));
        Assert.Throws<ArgumentException>(() => ProviderAccountSecret.NormalizeLabel("   "));
        Assert.Throws<ArgumentException>(() => ProviderAccountSecret.NormalizeLabel(new string('x', 81)));
        Assert.Throws<ArgumentException>(() => ProviderAccountSecret.NormalizeLabel("a\nb"));
    }
}
