// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Live verification of LineMessagingProvider against the real LINE Messaging API. Marked Explicit,
/// so it never runs in the normal suite or in CI. Credentials come from environment variables
/// and are never stored in the repository.
///
/// Run with:
///   powershell -File run-live-test.ps1 -Provider Line
///
/// LINE is the only one of the four Asian messengers without a conversation window: push messages
/// may be sent at any time, so unlike WhatsApp there is no precondition to arrange before the run.
/// The recipient must have added the bot as a friend - that is LINE's equivalent of an allow list.
///
/// Two steps here go beyond the Slack and WhatsApp fixtures and answer open questions rather than
/// asserting a remembered value: Step2 reads the real free-tier quota before anything is sent, and
/// Step6 measures where LINE actually rejects an over-long message (no adapter checks message
/// length today). Both log the outcome instead of asserting a specific limit.
///
/// A run consumes up to three messages from the monthly free allowance (Step4, Step5, Step6).
///
/// Not covered here: inbound webhooks. Verifying ValidateWebhook against a genuine x-line-signature
/// requires a public HTTPS callback LINE can reach; recomputing the HMAC locally would only test our
/// own assumption against itself.
/// </summary>
using System.Net.Http.Headers;
using System.Text.Json;
using Klacks.Plugin.Messaging.Infrastructure.Http;
using Klacks.Plugin.Messaging.Infrastructure.Services.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging.Live;

[TestFixture]
[Explicit("Sends real messages through a real LINE Official Account. Requires LINE_CHANNEL_ACCESS_TOKEN and LINE_USER_ID.")]
[Category("Live")]
public class LineLiveVerificationTests
{
    private const string TokenVariable = "LINE_CHANNEL_ACCESS_TOKEN";
    private const string UserIdVariable = "LINE_USER_ID";
    private const string ChannelSecretVariable = "LINE_CHANNEL_SECRET";

    private const string BotInfoUrl = "https://api.line.me/v2/bot/info";
    private const string QuotaUrl = "https://api.line.me/v2/bot/message/quota";
    private const string ConsumptionUrl = "https://api.line.me/v2/bot/message/quota/consumption";
    private const string BearerScheme = "Bearer";
    private const string BasicIdProperty = "basicId";

    // Syntactically valid LINE user id (U + 32 hex) that no real account holds, so the error path
    // cannot reach a person. A push to a user who has not added the bot fails regardless.
    private const string UnknownUserId = "U00000000000000000000000000000000";

    // Deliberately above every documented LINE text limit, to find out where the real boundary is.
    private const int OverlongMessageLength = 5001;

    private HttpClient? _httpClient;
    private LineMessagingProvider _provider = null!;
    private string _accessToken = null!;
    private string _userId = null!;
    private string _configJson = null!;

    [SetUp]
    public void Setup()
    {
        _accessToken = RequireVariable(TokenVariable);
        _userId = RequireVariable(UserIdVariable);
        _configJson = BuildConfig(_accessToken);

        var retryHandler = new RateLimitRetryHandler(NullLogger<RateLimitRetryHandler>.Instance)
        {
            InnerHandler = new HttpClientHandler()
        };
        _httpClient = new HttpClient(retryHandler);
        _provider = new LineMessagingProvider(_httpClient, NullLogger<LineMessagingProvider>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient?.Dispose();
        _httpClient = null;
    }

    [Test]
    [Order(1)]
    public async Task Step1_ValidateConfig_Accepts_The_Real_Channel_Access_Token()
    {
        var isValid = await _provider.ValidateConfigAsync(_configJson);

        isValid.ShouldBeTrue(
            $"/v2/bot/info rejected the token in {TokenVariable}. Channel access tokens are issued " +
            "per channel in the LINE Developers Console; short-lived ones expire.");
    }

    [Test]
    [Order(2)]
    public async Task Step2_The_Free_Tier_Quota_Is_Readable()
    {
        var quota = await GetAsync(QuotaUrl);
        var consumption = await GetAsync(ConsumptionUrl);

        // Runs before anything is sent, so the remaining allowance is known up front - the later
        // steps consume up to three messages from it. The response shape is logged rather than
        // asserted; it could not be retrieved from the documentation.
        TestContext.Out.WriteLine($"Quota:       {quota}");
        TestContext.Out.WriteLine($"Consumption: {consumption}");

        quota.ShouldNotBeNullOrWhiteSpace("the monthly quota endpoint returned nothing");
        consumption.ShouldNotBeNullOrWhiteSpace("the consumption endpoint returned nothing");
    }

    [Test]
    [Order(3)]
    public async Task Step3_The_Token_Belongs_To_A_Messaging_Api_Channel()
    {
        var body = await GetAsync(BotInfoUrl);

        TestContext.Out.WriteLine($"Bot info: {body}");

        var json = JsonSerializer.Deserialize<JsonElement>(body);
        json.ValueKind.ShouldBe(JsonValueKind.Object, $"/v2/bot/info did not answer with a JSON object: {body}");

        // basicId is a required field of BotInfoResponse in LINE's own OpenAPI definition
        // (github.com/line/line-openapi, messaging-api.yml).
        json.TryGetProperty(BasicIdProperty, out _).ShouldBeTrue(
            $"/v2/bot/info answered without {BasicIdProperty}. Either the token in {TokenVariable} does " +
            "not belong to a Messaging API channel, or the response shape changed - the full body is " +
            "in the log above.");
    }

    [Test]
    [Order(4)]
    public async Task Step4_SendAsync_Reports_A_Real_Line_Error_For_An_Unknown_Recipient()
    {
        var result = await _provider.SendAsync(new(UnknownUserId, "Klacks live verification - error path"), _configJson);

        TestContext.Out.WriteLine($"LINE response: success={result.Success} id={result.ExternalMessageId} error={result.ErrorMessage}");

        result.Success.ShouldBeFalse(
            "LINE accepted a push to a user id nobody holds. Either the error path is untested, or " +
            "/v2/bot/message/push acknowledges before resolving the recipient - in which case " +
            "SendMessageResult.Success means 'accepted', not 'delivered'. Check the logged id.");
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace(
            "the LINE error body was not surfaced, so ExtractErrorMessage does not match the real payload shape");
    }

    [Test]
    [Order(5)]
    public async Task Step5_SendAsync_Delivers_A_Push_Message_To_The_Real_Account()
    {
        var text = $"Klacks live verification - outbound push, run {TestContext.CurrentContext.Test.ID}";

        var result = await _provider.SendAsync(new(_userId, text), _configJson);

        TestContext.Out.WriteLine($"LINE response: success={result.Success} id={result.ExternalMessageId} error={result.ErrorMessage}");

        result.Success.ShouldBeTrue(
            $"LINE refused the push: {result.ErrorMessage}. If this mentions the recipient, the account " +
            $"in {UserIdVariable} has not added the bot as a friend - LINE's equivalent of an allow list.");
        result.IsThrottled.ShouldBeFalse();
    }

    [Test]
    [Order(6)]
    public async Task Step6_An_Overlong_Message_Is_Reported_Consistently()
    {
        var text = new string('K', OverlongMessageLength);

        var result = await _provider.SendAsync(new(_userId, text), _configJson);

        TestContext.Out.WriteLine(
            $"Length probe at {OverlongMessageLength} chars: success={result.Success} error={result.ErrorMessage}");

        // No adapter checks message length, so this measures LINE's real boundary instead of
        // asserting a remembered number. Either outcome is valid - what must hold is that the
        // adapter reports it truthfully rather than swallowing it.
        if (result.Success)
        {
            result.ExternalMessageId.ShouldNotBeNullOrWhiteSpace(
                $"LINE accepted {OverlongMessageLength} characters but the adapter returned no message id");
        }
        else
        {
            result.ErrorMessage.ShouldNotBeNullOrWhiteSpace(
                $"LINE rejected {OverlongMessageLength} characters but the adapter surfaced no reason, " +
                "so a length rejection would reach the user as a silent failure");
        }
    }

    private async Task<string> GetAsync(string url)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, _accessToken);

        var response = await _httpClient!.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        response.IsSuccessStatusCode.ShouldBeTrue($"GET {url} failed: {response.StatusCode} - {body}");
        return body;
    }

    private static string BuildConfig(string accessToken)
    {
        var channelSecret = Environment.GetEnvironmentVariable(ChannelSecretVariable) ?? string.Empty;
        return JsonSerializer.Serialize(new
        {
            ChannelAccessToken = accessToken,
            ChannelSecret = channelSecret,
        });
    }

    private static string RequireVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            Assert.Ignore($"Environment variable {name} is not set. See the class summary for how to run this.");

        return value!;
    }
}
