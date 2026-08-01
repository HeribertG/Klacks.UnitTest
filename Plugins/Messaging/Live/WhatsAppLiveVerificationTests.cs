// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Live verification of WhatsAppMessagingProvider against the real Meta Graph API. Marked Explicit,
/// so it never runs in the normal suite or in CI. Credentials come from environment variables
/// and are never stored in the repository.
///
/// Run with:
///   powershell -File run-live-test.ps1 -Provider WhatsApp
///
/// The decisive question this fixture answers is Step3: WhatsApp Cloud API accepts a plain
/// text message only while a customer service window is open, which the recipient opens by
/// messaging the business number. A business-initiated first contact must use a template.
/// The provider hardcodes type=text, so Step3 failing means the provider structurally cannot
/// open a conversation - not that the plumbing is broken. Reply from the recipient phone to
/// the test number immediately before running, otherwise Step3 measures the window, not the code.
///
/// Not covered here: inbound webhooks. Verifying ValidateWebhook against a genuine Meta
/// signature requires a public HTTPS callback that Meta can reach; recomputing the HMAC locally
/// would only test our own assumption against itself.
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
[Explicit("Sends real messages through a real Meta WhatsApp Business account. Requires WHATSAPP_ACCESS_TOKEN, WHATSAPP_PHONE_NUMBER_ID and WHATSAPP_RECIPIENT.")]
[Category("Live")]
public class WhatsAppLiveVerificationTests
{
    private const string TokenVariable = "WHATSAPP_ACCESS_TOKEN";
    private const string PhoneNumberIdVariable = "WHATSAPP_PHONE_NUMBER_ID";
    private const string RecipientVariable = "WHATSAPP_RECIPIENT";
    private const string AppSecretVariable = "WHATSAPP_APP_SECRET";

    private const string GraphApiBaseUrl = "https://graph.facebook.com";
    private const string GraphApiVersion = "v21.0";
    private const string PhoneNumberNodeFields = "?fields=display_phone_number,verified_name";
    private const string BearerScheme = "Bearer";
    private const string DisplayPhoneNumberProperty = "display_phone_number";

    // Reserved for fictional use by NANPA (+1 XXX 555-0100..0199), so it belongs to nobody and
    // is never on the test number allow list. A real unlisted number would risk a real delivery
    // once this runs against a production sender.
    private const string UnreachableRecipient = "12025550199";

    private HttpClient? _httpClient;
    private WhatsAppMessagingProvider _provider = null!;
    private string _accessToken = null!;
    private string _phoneNumberId = null!;
    private string _recipient = null!;
    private string _configJson = null!;

    [SetUp]
    public void Setup()
    {
        _accessToken = RequireVariable(TokenVariable);
        _phoneNumberId = RequireVariable(PhoneNumberIdVariable);
        _recipient = RequireVariable(RecipientVariable);
        _configJson = BuildConfig(_accessToken, _phoneNumberId);

        var retryHandler = new RateLimitRetryHandler(NullLogger<RateLimitRetryHandler>.Instance)
        {
            InnerHandler = new HttpClientHandler()
        };
        _httpClient = new HttpClient(retryHandler);
        _provider = new WhatsAppMessagingProvider(_httpClient, NullLogger<WhatsAppMessagingProvider>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient?.Dispose();
        _httpClient = null;
    }

    [Test]
    [Order(1)]
    public async Task Step1_ValidateConfig_Accepts_The_Real_Access_Token()
    {
        var isValid = await _provider.ValidateConfigAsync(_configJson);

        isValid.ShouldBeTrue(
            $"Meta rejected the credentials in {TokenVariable} / {PhoneNumberIdVariable}. " +
            "Temporary tokens from the Meta app dashboard expire after 24 hours.");
    }

    [Test]
    [Order(2)]
    public async Task Step2_The_Configured_Id_Is_A_Phone_Number_Node_Not_A_Business_Account()
    {
        var url = $"{GraphApiBaseUrl}/{GraphApiVersion}/{_phoneNumberId}{PhoneNumberNodeFields}";
        using var request = new HttpRequestMessage(HttpMethod.Get, url);
        request.Headers.Authorization = new AuthenticationHeaderValue(BearerScheme, _accessToken);

        var response = await _httpClient!.SendAsync(request);
        var body = await response.Content.ReadAsStringAsync();

        TestContext.Out.WriteLine($"Phone number node: {response.StatusCode} - {body}");

        response.IsSuccessStatusCode.ShouldBeTrue(
            "the Graph node could not be read with these fields");

        var json = JsonSerializer.Deserialize<JsonElement>(body);
        json.TryGetProperty(DisplayPhoneNumberProperty, out _).ShouldBeTrue(
            $"the id in {PhoneNumberIdVariable} answers to ?fields=id but has no {DisplayPhoneNumberProperty}, " +
            "so it is a WhatsApp Business Account id, not a phone number id. ValidateConfigAsync " +
            "cannot tell these apart because it only asks for ?fields=id, so every send would fail " +
            "while the configuration looks valid.");
    }

    [Test]
    [Order(3)]
    public async Task Step3_SendAsync_Reports_A_Real_Meta_Error_For_An_Unreachable_Recipient()
    {
        var result = await _provider.SendAsync(new(UnreachableRecipient, "Klacks live verification - error path"), _configJson);

        TestContext.Out.WriteLine($"Meta response: success={result.Success} id={result.ExternalMessageId} error={result.ErrorMessage}");

        result.Success.ShouldBeFalse(
            "Meta accepted a send to a number nobody owns. Either the error path is untested, or - " +
            "the more serious reading - /messages answered 200 with a wamid and will only report " +
            "undeliverability later through a status webhook. In that case the provider reports " +
            "success for messages that never arrive, and SendMessageResult.Success means 'accepted', " +
            "not 'delivered'. Check the logged id before treating this as a test defect.");
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace(
            "the Meta error body was not surfaced, so ExtractErrorMessage did not match the real payload shape");
        result.IsThrottled.ShouldBeFalse("an unreachable recipient is a permanent error, not throttling");
    }

    [Test]
    [Order(4)]
    public async Task Step4_SendAsync_Delivers_A_Text_Message_To_The_Allow_Listed_Recipient()
    {
        var text = $"Klacks live verification - outbound send, run {TestContext.CurrentContext.Test.ID}";

        var result = await _provider.SendAsync(new(_recipient, text), _configJson);

        TestContext.Out.WriteLine($"Meta response: success={result.Success} id={result.ExternalMessageId} error={result.ErrorMessage}");

        result.Success.ShouldBeTrue(
            $"Meta refused the text send: {result.ErrorMessage}. If this names a re-engagement or " +
            "24 hour window restriction, the provider is working but cannot start a conversation: " +
            "it only ever sends type=text, and a business-initiated first contact requires type=template.");
        result.ExternalMessageId.ShouldNotBeNullOrWhiteSpace("Meta returned no message id (wamid)");
        result.IsThrottled.ShouldBeFalse();
    }

    private static string BuildConfig(string accessToken, string phoneNumberId)
    {
        var appSecret = Environment.GetEnvironmentVariable(AppSecretVariable) ?? string.Empty;
        return JsonSerializer.Serialize(new
        {
            AccessToken = accessToken,
            PhoneNumberId = phoneNumberId,
            AppSecret = appSecret,
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
