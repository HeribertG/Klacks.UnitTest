// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Live verification of SignalMessagingProvider against a real signal-cli-rest-api container.
/// Marked Explicit, so it never runs in the normal suite or in CI. Credentials come from
/// environment variables and are never stored in the repository.
///
/// Run with:
///   powershell -File run-live-test.ps1 -Provider Signal
///
/// Unlike the other providers, Signal has no cloud service: it needs a self-hosted
/// signal-cli-rest-api container with a registered sender number. Setting that up is the real
/// cost here - see signal-live-setup.md.
///
/// Step1 checks container reachability on its own, before Step2 validates the configuration.
/// ValidateConfigAsync returns false for both "container unreachable" and "number not in the
/// account list", so without the split a red Step2 would be ambiguous.
///
/// Signal is outbound-only by design: ValidateWebhook returns false and ParseWebhookPayload
/// returns null, and the shipped manual says so. The absent inbound steps are not an oversight.
/// </summary>
using System.Text.Json;
using Klacks.Plugin.Messaging.Infrastructure.Http;
using Klacks.Plugin.Messaging.Infrastructure.Services.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging.Live;

[TestFixture]
[Explicit("Sends real messages through a self-hosted signal-cli container. Requires SIGNAL_API_URL, SIGNAL_NUMBER and SIGNAL_RECIPIENT.")]
[Category("Live")]
public class SignalLiveVerificationTests
{
    private const string ApiUrlVariable = "SIGNAL_API_URL";
    private const string NumberVariable = "SIGNAL_NUMBER";
    private const string RecipientVariable = "SIGNAL_RECIPIENT";

    private const string AccountsPath = "v1/accounts";

    // Reserved for fictional use by NANPA (+1 XXX 555-0100..0199), so it belongs to nobody and
    // is certainly not a Signal account - a real unlisted number could reach a stranger.
    private const string UnregisteredRecipient = "+12025550199";

    private HttpClient? _httpClient;
    private SignalMessagingProvider _provider = null!;
    private string _apiUrl = null!;
    private string _number = null!;
    private string _recipient = null!;
    private string _configJson = null!;

    [SetUp]
    public void Setup()
    {
        _apiUrl = RequireVariable(ApiUrlVariable).TrimEnd('/');
        _number = RequireVariable(NumberVariable);
        _recipient = RequireVariable(RecipientVariable);
        _configJson = JsonSerializer.Serialize(new { SignalNumber = _number, ApiUrl = _apiUrl });

        var retryHandler = new RateLimitRetryHandler(NullLogger<RateLimitRetryHandler>.Instance)
        {
            InnerHandler = new HttpClientHandler()
        };
        _httpClient = new HttpClient(retryHandler);
        _provider = new SignalMessagingProvider(_httpClient, NullLogger<SignalMessagingProvider>.Instance);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient?.Dispose();
        _httpClient = null;
    }

    [Test]
    [Order(1)]
    public async Task Step1_The_Signal_Cli_Container_Is_Reachable()
    {
        var url = $"{_apiUrl}/{AccountsPath}";

        HttpResponseMessage response;
        try
        {
            response = await _httpClient!.GetAsync(url);
        }
        catch (HttpRequestException ex)
        {
            Assert.Fail(
                $"No signal-cli-rest-api answered at {url}: {ex.Message}. " +
                "Start the container before running this - see signal-live-setup.md.");
            return;
        }

        var body = await response.Content.ReadAsStringAsync();
        TestContext.Out.WriteLine($"Registered accounts: {response.StatusCode} - {body}");

        response.IsSuccessStatusCode.ShouldBeTrue($"the container answered {response.StatusCode} at {url}");
    }

    [Test]
    [Order(2)]
    public async Task Step2_ValidateConfig_Finds_The_Sender_Number_In_The_Account_List()
    {
        var isValid = await _provider.ValidateConfigAsync(_configJson);

        isValid.ShouldBeTrue(
            $"the container is reachable (Step1) but {NumberVariable} is not in its account list. " +
            "The number has to be registered or linked in signal-cli first.");
    }

    [Test]
    [Order(3)]
    public async Task Step3_SendAsync_Reports_A_Real_Signal_Error_For_An_Unregistered_Recipient()
    {
        var result = await _provider.SendAsync(new(UnregisteredRecipient, "Klacks live verification - error path"), _configJson);

        TestContext.Out.WriteLine($"Signal response: success={result.Success} id={result.ExternalMessageId} error={result.ErrorMessage}");

        result.Success.ShouldBeFalse(
            "signal-cli accepted a send to a number that holds no Signal account. Either the error " +
            "path is untested, or /v2/send acknowledges before resolving the recipient - in which " +
            "case SendMessageResult.Success means 'accepted', not 'delivered'.");
        result.ErrorMessage.ShouldNotBeNullOrWhiteSpace(
            "the signal-cli error body was not surfaced, so the error extraction does not match the real payload shape");
    }

    [Test]
    [Order(4)]
    public async Task Step4_SendAsync_Delivers_A_Message_To_The_Real_Recipient()
    {
        var text = $"Klacks live verification - outbound send, run {TestContext.CurrentContext.Test.ID}";

        var result = await _provider.SendAsync(new(_recipient, text), _configJson);

        TestContext.Out.WriteLine($"Signal response: success={result.Success} id={result.ExternalMessageId} error={result.ErrorMessage}");

        result.Success.ShouldBeTrue($"signal-cli refused the send: {result.ErrorMessage}");
        result.ExternalMessageId.ShouldNotBeNullOrWhiteSpace(
            "signal-cli returned no timestamp, so the adapter cannot report which message it sent");
        result.IsThrottled.ShouldBeFalse();
    }

    private static string RequireVariable(string name)
    {
        var value = Environment.GetEnvironmentVariable(name);
        if (string.IsNullOrWhiteSpace(value))
            Assert.Ignore($"Environment variable {name} is not set. See the class summary for how to run this.");

        return value!;
    }
}
