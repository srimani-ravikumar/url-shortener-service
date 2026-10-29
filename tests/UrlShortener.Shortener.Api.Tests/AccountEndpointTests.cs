using System.Net;
using System.Net.Http.Json;
using UrlShortener.Core.Security;
using UrlShortener.Core.Storage;

namespace UrlShortener.Shortener.Api.Tests;

/// <summary>Tests sign-up, key recovery, verification and the "me" endpoints through the real HTTP pipeline.</summary>
public class AccountEndpointTests : ApiTestBase
{
    private const string Address = "new.user@example.com";

    [Test]
    public async Task SignUp_Returns202_AndMailsAVerificationLinkToTheNormalizedAddress()
    {
        using var client = Factory.CreateClient();

        var response = await client.SignUp("  New.User@Example.COM ");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        var mail = Factory.Mail.Sent.Single();
        Assert.That(mail.To, Is.EqualTo(Address));
        Assert.That(mail.Body, Does.Contain($"{ShortenerApiFactory.WebBaseUrl}/app/verify#token="));
        Assert.That(Factory.Tokens.Count, Is.EqualTo(1));
        Assert.That(Factory.Tokens.LastLifetime, Is.EqualTo(TimeSpan.FromHours(24)));
    }

    [Test]
    public async Task SignUp_PutsTheTokenInTheFragment_SoItNeverReachesAServer()
    {
        using var client = Factory.CreateClient();

        await client.SignUp(Address);

        var token = Factory.Mail.TokenSentTo(Address);
        Assert.That(Factory.Mail.Sent.Single().Body, Does.Not.Contain("?token=").And.Not.Contain("&token="));
        Assert.That(token, Has.Length.EqualTo(43));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-an-email")]
    public async Task SignUp_Returns400_ForAnInvalidAddress(string? email)
    {
        using var client = Factory.CreateClient();

        var response = await client.SignUp(email);
        var problem = await response.ReadJson();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(problem.GetProperty("errors").TryGetProperty("email", out _), Is.True);
        Assert.That(Factory.Mail.Sent, Is.Empty);
    }

    [Test]
    public async Task SignUp_GivesTheSameAnswer_ForAKnownAndAnUnknownAddress()
    {
        Factory.Users.AddUser("existing", "known@example.com");
        using var client = Factory.CreateClient();

        var known = await client.SignUp("known@example.com");
        var unknown = await client.SignUp("unknown@example.com");

        Assert.That(known.StatusCode, Is.EqualTo(unknown.StatusCode));
        Assert.That(await known.Content.ReadAsStringAsync(), Is.EqualTo(await unknown.Content.ReadAsStringAsync()));
    }

    [Test]
    public async Task SignUp_ForAKnownAddress_MailsAHintInsteadOfAToken()
    {
        Factory.Users.AddUser("existing", "known@example.com");
        using var client = Factory.CreateClient();

        await client.SignUp("known@example.com");

        var mail = Factory.Mail.Sent.Single();
        Assert.That(mail.Subject, Does.Contain("already have an account"));
        Assert.That(mail.Body, Does.Contain("/app/recover"));
        Assert.That(mail.Body, Does.Not.Contain("token="));
        Assert.That(Factory.Tokens.Count, Is.Zero);
        Assert.That(Factory.Users.Accounts, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task SignUp_SendsOnlyOneMailPerAddressWhileTheCooldownRuns()
    {
        using var client = Factory.CreateClient();

        var first = await client.SignUp(Address);
        var second = await client.SignUp(Address.ToUpperInvariant());

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(Factory.Mail.Sent, Has.Count.EqualTo(1));
        Assert.That(Factory.Cooldowns.LastDuration, Is.EqualTo(TimeSpan.FromSeconds(60)));

        Factory.Cooldowns.ExpireAll();
        await client.SignUp(Address);
        Assert.That(Factory.Mail.Sent, Has.Count.EqualTo(2));
    }

    [Test]
    public async Task SignUp_Returns503_WhenTheMailServerIsDown()
    {
        Factory.Mail.Fail = true;
        using var client = Factory.CreateClient();

        var response = await client.SignUp(Address);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(response.Headers.RetryAfter, Is.Not.Null);
        Assert.That(Factory.Users.Accounts, Is.Empty);
    }

    [Test]
    public async Task SignUp_Returns429_WhenOneAddressSendsTooManyRequests()
    {
        Factory.Settings["Account:MailRequestsPerHour"] = "2";
        using var client = Factory.CreateClient();

        var statuses = new List<HttpStatusCode>();
        for (var i = 0; i < 3; i++)
        {
            statuses.Add((await client.SignUp($"user{i}@example.com")).StatusCode);
        }

        Assert.That(statuses, Is.EqualTo(new[] { HttpStatusCode.Accepted, HttpStatusCode.Accepted, HttpStatusCode.TooManyRequests }));
    }

    [Test]
    public async Task Verify_CreatesTheAccount_AndTheReturnedKeyWorks()
    {
        using var client = Factory.CreateClient();
        await client.SignUp(Address);

        var response = await client.Verify(Factory.Mail.TokenSentTo(Address));
        var credentials = await response.ReadJson();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        Assert.That(credentials.GetProperty("email").GetString(), Is.EqualTo(Address));

        using var signedIn = Factory.CreateClient();
        signedIn.DefaultRequestHeaders.Add("X-Api-Key", credentials.GetProperty("api_key").GetString());
        var me = await signedIn.GetAsync("/api/v1/users/me");
        var account = await me.ReadJson();

        Assert.That(me.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(account.GetProperty("user_id").GetString(), Is.EqualTo(credentials.GetProperty("user_id").GetString()));
        Assert.That(account.GetProperty("email").GetString(), Is.EqualTo(Address));
    }

    [Test]
    public async Task Verify_StoresOnlyTheHashOfTheKey()
    {
        using var client = Factory.CreateClient();
        await client.SignUp(Address);

        var credentials = await (await client.Verify(Factory.Mail.TokenSentTo(Address))).ReadJson();

        var stored = Factory.Users.Accounts.Single();
        Assert.That(stored.ApiKeyHash, Is.EqualTo(ApiKeyHasher.Hash(credentials.GetProperty("api_key").GetString()!)));
    }

    [Test]
    public async Task Verify_Returns400_WhenATokenIsUsedTwice()
    {
        using var client = Factory.CreateClient();
        await client.SignUp(Address);
        var token = Factory.Mail.TokenSentTo(Address);

        var first = await client.Verify(token);
        var second = await client.Verify(token);

        Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(second.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(Factory.Users.Accounts, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Verify_Returns400_ForAnExpiredToken()
    {
        using var client = Factory.CreateClient();
        await client.SignUp(Address);
        var token = Factory.Mail.TokenSentTo(Address);
        Factory.Tokens.Clear();

        var response = await client.Verify(token);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(Factory.Users.Accounts, Is.Empty);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-a-real-token")]
    public async Task Verify_Returns400_ForAnUnknownToken(string? token)
    {
        using var client = Factory.CreateClient();

        var response = await client.Verify(token);
        var problem = await response.ReadJson();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest));
        Assert.That(problem.GetProperty("errors").TryGetProperty("token", out _), Is.True);
    }

    [Test]
    public async Task Verify_Returns409_WhenTheAddressGotAnAccountInTheMeantime()
    {
        using var client = Factory.CreateClient();
        await client.SignUp(Address);
        var token = Factory.Mail.TokenSentTo(Address);
        Factory.Users.AddUser("someone-else", Address);

        var response = await client.Verify(token);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Conflict));
        Assert.That(Factory.Users.Accounts, Has.Count.EqualTo(1));
    }

    [Test]
    public async Task Recover_ForAnUnknownAddress_Returns202WithoutSendingAnything()
    {
        using var client = Factory.CreateClient();

        var response = await client.PostAsJsonAsync("/api/v1/recover", new { Email = "nobody@example.com" }, ApiJson.Options);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted));
        Assert.That(Factory.Mail.Sent, Is.Empty);
        Assert.That(Factory.Tokens.Count, Is.Zero);
    }

    [Test]
    public async Task Recover_ReplacesTheLostKey_AndTheOldOneStopsWorking()
    {
        var oldKey = Factory.Users.AddUser("user-1", Address);
        using var client = Factory.CreateClient();

        await client.PostAsJsonAsync("/api/v1/recover", new { Email = Address }, ApiJson.Options);
        var response = await client.Verify(Factory.Mail.TokenSentTo(Address));
        var credentials = await response.ReadJson();

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(credentials.GetProperty("user_id").GetString(), Is.EqualTo("user-1"));
        Assert.That(await StatusOfMe(oldKey), Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await StatusOfMe(credentials.GetProperty("api_key").GetString()!), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(Factory.Users.KeyCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Me_RequiresAnApiKey()
    {
        using var client = Factory.CreateClient();

        var response = await client.GetAsync("/api/v1/users/me");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Rotate_IssuesANewKey_AndRevokesTheOldOne()
    {
        var oldKey = Factory.Users.AddUser("user-1", Address);
        using var client = ClientWithKey(oldKey);

        var response = await client.PostAsync("/api/v1/users/me/api-key/rotate", content: null);
        var credentials = await response.ReadJson();
        var newKey = credentials.GetProperty("api_key").GetString()!;

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That(response.Headers.CacheControl?.NoStore, Is.True);
        Assert.That(newKey, Is.Not.EqualTo(oldKey));
        Assert.That(await StatusOfMe(oldKey), Is.EqualTo(HttpStatusCode.Unauthorized));
        Assert.That(await StatusOfMe(newKey), Is.EqualTo(HttpStatusCode.OK));
        Assert.That(Factory.Users.KeyCount, Is.EqualTo(1));
    }

    [Test]
    public async Task Rotate_RequiresAnApiKey()
    {
        using var client = Factory.CreateClient();

        var response = await client.PostAsync("/api/v1/users/me/api-key/rotate", content: null);

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Delete_RemovesTheAccount_ItsLinks_AndTheirCachedAndCountedData()
    {
        var key = Factory.Users.AddUser("owner", Address);
        Factory.Users.AddUser("bystander", "bystander@example.com");
        await AddLinkWithDataAsync("owner", "own-link-1", minutesOld: 1);
        await AddLinkWithDataAsync("owner", "own-link-2", minutesOld: 2);
        await AddLinkWithDataAsync("bystander", "their-link", minutesOld: 3);
        using var client = ClientWithKey(key);

        var response = await client.DeleteAsync("/api/v1/users/me");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(Factory.Links.Stored.Keys, Is.EquivalentTo(new[] { "their-link" }));
        Assert.That(Factory.Cache.Entries.Keys, Is.EquivalentTo(new[] { "their-link" }));
        Assert.That(Factory.Clicks.Stats.Keys, Is.EquivalentTo(new[] { "their-link" }));
        Assert.That(Factory.ClickBuffer.Clicks.Count, Is.EqualTo(3)); // everything recorded stays in the audit list...
        Assert.That(await Factory.ClickBuffer.TakeAsync("own-link-1"), Is.Null); // ...but nothing is left to flush
        Assert.That(Factory.Users.Accounts.Select(account => account.UserId), Is.EquivalentTo(new[] { "bystander" }));
        Assert.That(await StatusOfMe(key), Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Delete_RemovesLinksOnEveryPage()
    {
        var key = Factory.Users.AddUser("owner", Address);
        for (var i = 0; i < 250; i++)
        {
            Factory.Links.Add(NewLink("owner", $"code-{i:D3}", minutesOld: i));
        }

        using var client = ClientWithKey(key);

        var response = await client.DeleteAsync("/api/v1/users/me");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(Factory.Links.Stored, Is.Empty);
    }

    [Test]
    public async Task Delete_FreesTheAddressForANewAccount()
    {
        var key = Factory.Users.AddUser("owner", Address);
        using var client = ClientWithKey(key);
        await client.DeleteAsync("/api/v1/users/me");

        using var anonymous = Factory.CreateClient();
        await anonymous.SignUp(Address);
        var response = await anonymous.Verify(Factory.Mail.TokenSentTo(Address));

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK));
        Assert.That((await response.ReadJson()).GetProperty("user_id").GetString(), Is.Not.EqualTo("owner"));
    }

    [Test]
    public async Task Delete_Returns503AndKeepsTheAccount_WhenTheCacheCannotBeCleared()
    {
        var key = Factory.Users.AddUser("owner", Address);
        await AddLinkWithDataAsync("owner", "own-link", minutesOld: 1);
        Factory.Cache.Unavailable = true;
        using var client = ClientWithKey(key);

        var response = await client.DeleteAsync("/api/v1/users/me");

        // The key still works, so the caller can simply try again once Redis is back.
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.ServiceUnavailable));
        Assert.That(await StatusOfMe(key), Is.EqualTo(HttpStatusCode.OK));

        Factory.Cache.Unavailable = false;
        var retry = await client.DeleteAsync("/api/v1/users/me");
        Assert.That(retry.StatusCode, Is.EqualTo(HttpStatusCode.NoContent));
        Assert.That(Factory.Links.Stored, Is.Empty);
    }

    [Test]
    public async Task Delete_RequiresAnApiKey()
    {
        using var client = Factory.CreateClient();

        var response = await client.DeleteAsync("/api/v1/users/me");

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized));
    }

    [Test]
    public async Task Secrets_NeverAppearInTheLogs()
    {
        using var client = Factory.CreateClient();
        await client.SignUp(Address);
        var token = Factory.Mail.TokenSentTo(Address);
        var credentials = await (await client.Verify(token)).ReadJson();
        using var signedIn = ClientWithKey(credentials.GetProperty("api_key").GetString()!);
        var rotated = await (await signedIn.PostAsync("/api/v1/users/me/api-key/rotate", content: null)).ReadJson();

        var logs = Factory.Logs.AllText;

        Assert.That(logs, Does.Not.Contain(token));
        Assert.That(logs, Does.Not.Contain(credentials.GetProperty("api_key").GetString()!));
        Assert.That(logs, Does.Not.Contain(rotated.GetProperty("api_key").GetString()!));
        Assert.That(logs, Does.Not.Contain(Address));
    }

    private HttpClient ClientWithKey(string apiKey)
    {
        var client = Factory.CreateClient();
        client.DefaultRequestHeaders.Add("X-Api-Key", apiKey);
        return client;
    }

    private async Task<HttpStatusCode> StatusOfMe(string apiKey)
    {
        using var client = ClientWithKey(apiKey);
        return (await client.GetAsync("/api/v1/users/me")).StatusCode;
    }

    private UrlMapping NewLink(string userId, string code, int minutesOld)
    {
        var created = Factory.Time.Now.AddMinutes(-minutesOld);
        return new UrlMapping(code, "https://example.com/" + code, userId, created, created.AddDays(30), IsCustom: false);
    }

    // A link plus everything the other services keep about it: a cache entry, statistics and buffered clicks.
    private async Task AddLinkWithDataAsync(string userId, string code, int minutesOld)
    {
        var link = NewLink(userId, code, minutesOld);
        Factory.Links.Add(link);
        await Factory.Cache.SetAsync(code, new CachedUrl(link.LongUrl, link.ExpiresAt), TimeSpan.FromHours(1));
        await Factory.Clicks.AddClicksAsync(code, 5, Factory.Time.Now);
        await Factory.ClickBuffer.RecordAsync(code, Factory.Time.Now);
    }
}

/// <summary>Request helpers for the account endpoints.</summary>
internal static class AccountApi
{
    public static Task<HttpResponseMessage> SignUp(this HttpClient client, string? email) =>
        client.PostAsJsonAsync("/api/v1/signup", new { Email = email }, ApiJson.Options);

    public static Task<HttpResponseMessage> Verify(this HttpClient client, string? token) =>
        client.PostAsJsonAsync("/api/v1/verify", new { Token = token }, ApiJson.Options);
}
