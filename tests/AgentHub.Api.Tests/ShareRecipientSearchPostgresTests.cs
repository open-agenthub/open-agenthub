using AgentHub.Api.Persistence;
using Xunit;

namespace AgentHub.Api.Tests;

public class ShareRecipientSearchPostgresTests
{
    [PostgreSqlFact]
    public async Task Suggestions_AreBounded_ExcludeCaller_AndTreatWildcardsLiterally()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        var users = new UserDirectory(database.Configuration);
        await users.RecordLoginAsync("member-self", "self@example.com", "Member Self");
        for (var i = 0; i < 12; i++)
            await users.RecordLoginAsync($"member-{i:00}", $"person-{i}@example.com", $"Person {i}");
        var matches = await users.FindShareRecipientsAsync("member-self", "me", true);
        Assert.Equal(8, matches.Count);
        Assert.DoesNotContain(matches, user => user.Owner == "member-self");
        Assert.Empty(await users.FindShareRecipientsAsync("member-self", "m", true));
        Assert.Empty(await users.FindShareRecipientsAsync("member-self", "%", true));
        Assert.Single(await users.FindShareRecipientsAsync("member-self", "person-0@example.com", true));
    }

    [PostgreSqlFact]
    public async Task DisabledSuggestions_RequireExactUsernameOrEmail()
    {
        await using var database = await PostgresSharingDatabase.CreateAsync();
        var users = new UserDirectory(database.Configuration);
        await users.RecordLoginAsync("recipient", "recipient@example.com", "Recipient Name");
        Assert.Empty(await users.FindShareRecipientsAsync("owner", "rec", false));
        Assert.Equal("recipient", Assert.Single(await users.FindShareRecipientsAsync("owner", "recipient", false)).Owner);
        Assert.Equal("recipient", Assert.Single(await users.FindShareRecipientsAsync("owner", "RECIPIENT@example.com", false)).Owner);
        Assert.Empty(await users.FindShareRecipientsAsync("recipient", "recipient", false));
    }
}
