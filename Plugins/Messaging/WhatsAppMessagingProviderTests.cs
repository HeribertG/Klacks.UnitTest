// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for WhatsAppMessagingProvider covering message sending, config validation,
/// webhook signature validation, webhook payload parsing and subscription verification.
/// </summary>
using System.Net;
using System.Security.Cryptography;
using System.Text;
using Shouldly;
using Klacks.Plugin.Messaging.Domain.Models;
using Klacks.Plugin.Messaging.Infrastructure.Services.Providers;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class WhatsAppMessagingProviderTests
{
    private const string ValidConfigJson =
        "{\"AccessToken\":\"token-123\",\"PhoneNumberId\":\"106540352242922\",\"AppSecret\":\"app-secret\",\"VerifyToken\":\"verify-token\"}";

    private const string SignatureHeaderName = "X-Hub-Signature-256";

    private HttpClient _httpClient = null!;
    private FakeHttpMessageHandler _handler = null!;
    private ILogger<WhatsAppMessagingProvider> _logger = null!;
    private WhatsAppMessagingProvider _sut = null!;

    [SetUp]
    public void Setup()
    {
        _handler = new FakeHttpMessageHandler();
        _httpClient = new HttpClient(_handler);
        _logger = Substitute.For<ILogger<WhatsAppMessagingProvider>>();
        _sut = new WhatsAppMessagingProvider(_httpClient, _logger);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    [Test]
    public async Task SendAsync_With_Valid_Config_Sends_Correct_Request_And_Returns_MessageId()
    {
        // Arrange
        _handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(
                "{\"messaging_product\":\"whatsapp\",\"contacts\":[{\"input\":\"41791234567\",\"wa_id\":\"41791234567\"}],\"messages\":[{\"id\":\"wamid.XYZ\"}]}",
                Encoding.UTF8,
                "application/json")
        };

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("41791234567", "Hello from Klacks"), ValidConfigJson);

        // Assert
        result.Success.ShouldBeTrue();
        result.ExternalMessageId.ShouldBe("wamid.XYZ");
        _handler.LastRequest.ShouldNotBeNull();
        _handler.LastRequest!.Method.ShouldBe(HttpMethod.Post);
        _handler.LastRequest.RequestUri!.ToString().ShouldContain("106540352242922");
        _handler.LastRequest.RequestUri.ToString().ShouldContain("/messages");
        _handler.LastRequest.Headers.Authorization.ShouldNotBeNull();
        _handler.LastRequest.Headers.Authorization!.Scheme.ShouldBe("Bearer");
        _handler.LastRequest.Headers.Authorization.Parameter.ShouldBe("token-123");
        _handler.LastRequestBody.ShouldNotBeNull();
        _handler.LastRequestBody!.ShouldContain("\"to\":\"41791234567\"");
        _handler.LastRequestBody.ShouldContain("Hello from Klacks");
        _handler.LastRequestBody.ShouldContain("\"messaging_product\":\"whatsapp\"");
        _handler.LastRequestBody.ShouldContain("\"recipient_type\":\"individual\"");
    }

    [Test]
    public async Task SendAsync_On_Api_Error_Returns_Failure_With_Meta_Error_Message()
    {
        // Arrange
        _handler.Response = new HttpResponseMessage(HttpStatusCode.BadRequest)
        {
            Content = new StringContent(
                "{\"error\":{\"message\":\"Message failed to send because more than 24 hours have passed since the customer last replied to this number.\",\"type\":\"OAuthException\",\"code\":131047}}",
                Encoding.UTF8,
                "application/json")
        };

        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("41791234567", "Hello"), ValidConfigJson);

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNull();
        result.ErrorMessage!.ShouldContain("BadRequest");
        result.ErrorMessage.ShouldContain("more than 24 hours have passed");
    }

    [Test]
    public async Task SendAsync_With_Empty_Config_Returns_Failure_Without_Http_Call()
    {
        // Act
        var result = await _sut.SendAsync(new SendMessageRequest("41791234567", "Hello"), "{}");

        // Assert
        result.Success.ShouldBeFalse();
        result.ErrorMessage.ShouldNotBeNullOrEmpty();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task ValidateConfigAsync_With_Valid_Config_Returns_True()
    {
        // Arrange
        _handler.Response = new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent("{\"id\":\"106540352242922\"}", Encoding.UTF8, "application/json")
        };

        // Act
        var result = await _sut.ValidateConfigAsync(ValidConfigJson);

        // Assert
        result.ShouldBeTrue();
        _handler.LastRequest.ShouldNotBeNull();
        _handler.LastRequest!.RequestUri!.ToString().ShouldContain("fields=id");
        _handler.LastRequest.Headers.Authorization!.Parameter.ShouldBe("token-123");
    }

    [Test]
    public async Task ValidateConfigAsync_With_Missing_Fields_Returns_False_Without_Http_Call()
    {
        // Act
        var result = await _sut.ValidateConfigAsync("{\"AccessToken\":\"token-123\"}");

        // Assert
        result.ShouldBeFalse();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task ValidateConfigAsync_On_Api_Error_Returns_False()
    {
        // Arrange
        _handler.Response = new HttpResponseMessage(HttpStatusCode.Unauthorized);

        // Act
        var result = await _sut.ValidateConfigAsync(ValidConfigJson);

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public void ValidateWebhook_With_Correct_Signature_Returns_Valid()
    {
        // Arrange
        var body = "{\"entry\":[]}";
        var context = BuildWebhookContext(body, ComputeSignature(body, "app-secret"));

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeTrue();
    }

    [Test]
    public void ValidateWebhook_With_Uppercase_Hex_Signature_Returns_Valid()
    {
        // Arrange
        var body = "{\"entry\":[]}";
        var context = BuildWebhookContext(body, ComputeSignature(body, "app-secret").ToUpperInvariant().Replace("SHA256=", "sha256="));

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeTrue();
    }

    [Test]
    public void ValidateWebhook_With_Wrong_Signature_Returns_Invalid()
    {
        // Arrange
        var body = "{\"entry\":[]}";
        var context = BuildWebhookContext(body, ComputeSignature("tampered body", "app-secret"));

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeFalse();
    }

    [Test]
    public void ValidateWebhook_With_Missing_Signature_Header_Returns_Invalid()
    {
        // Arrange
        var context = new WebhookValidationContext(
            "{\"entry\":[]}",
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
            ValidConfigJson,
            string.Empty);

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeFalse();
    }

    [Test]
    public void ValidateWebhook_With_Missing_AppSecret_Returns_Invalid()
    {
        // Arrange
        var body = "{\"entry\":[]}";
        var configWithoutAppSecret = "{\"AccessToken\":\"token-123\",\"PhoneNumberId\":\"106540352242922\"}";
        var context = new WebhookValidationContext(
            body,
            new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            {
                [SignatureHeaderName] = ComputeSignature(body, "app-secret")
            },
            configWithoutAppSecret,
            string.Empty);

        // Act
        var result = _sut.ValidateWebhook(context);

        // Assert
        result.IsValid.ShouldBeFalse();
    }

    [Test]
    public void ParseWebhookPayload_With_Meta_Text_Message_Returns_Mapped_IncomingMessage()
    {
        // Arrange
        var body = """
        {
          "object": "whatsapp_business_account",
          "entry": [
            {
              "id": "102290129340398",
              "changes": [
                {
                  "value": {
                    "messaging_product": "whatsapp",
                    "metadata": { "display_phone_number": "15550783881", "phone_number_id": "106540352242922" },
                    "contacts": [ { "profile": { "name": "Kerry Fisher" }, "wa_id": "16505551234" } ],
                    "messages": [
                      {
                        "from": "16505551234",
                        "id": "wamid.ABGGFlA5Fpa",
                        "timestamp": "1603059201",
                        "text": { "body": "Hello this is an answer" },
                        "type": "text"
                      }
                    ]
                  },
                  "field": "messages"
                }
              ]
            }
          ]
        }
        """;

        // Act
        var result = _sut.ParseWebhookPayload(body);

        // Assert
        result.ShouldNotBeNull();
        result!.ExternalMessageId.ShouldBe("wamid.ABGGFlA5Fpa");
        result.Sender.ShouldBe("16505551234");
        result.SenderDisplayName.ShouldBe("Kerry Fisher");
        result.Content.ShouldBe("Hello this is an answer");
    }

    [Test]
    public void ParseWebhookPayload_Without_Contacts_Falls_Back_To_Sender_As_DisplayName()
    {
        // Arrange
        var body = """
        {
          "object": "whatsapp_business_account",
          "entry": [
            {
              "changes": [
                {
                  "value": {
                    "messaging_product": "whatsapp",
                    "messages": [
                      { "from": "16505551234", "id": "wamid.NOCONTACT", "text": { "body": "Hi" }, "type": "text" }
                    ]
                  }
                }
              ]
            }
          ]
        }
        """;

        // Act
        var result = _sut.ParseWebhookPayload(body);

        // Assert
        result.ShouldNotBeNull();
        result!.SenderDisplayName.ShouldBe("16505551234");
    }

    [Test]
    public void ParseWebhookPayload_With_Status_Update_Returns_Null()
    {
        // Arrange
        var body = """
        {
          "object": "whatsapp_business_account",
          "entry": [
            {
              "changes": [
                {
                  "value": {
                    "messaging_product": "whatsapp",
                    "statuses": [
                      { "id": "wamid.STATUS", "status": "delivered", "recipient_id": "16505551234" }
                    ]
                  },
                  "field": "messages"
                }
              ]
            }
          ]
        }
        """;

        // Act
        var result = _sut.ParseWebhookPayload(body);

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public void ParseWebhookPayload_With_NonText_Message_Returns_Null()
    {
        // Arrange
        var body = """
        {
          "entry": [
            {
              "changes": [
                {
                  "value": {
                    "messages": [
                      { "from": "16505551234", "id": "wamid.IMG", "type": "image", "image": { "id": "media-1" } }
                    ]
                  }
                }
              ]
            }
          ]
        }
        """;

        // Act
        var result = _sut.ParseWebhookPayload(body);

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public void ParseWebhookPayload_With_Garbage_Returns_Null()
    {
        // Act
        var result = _sut.ParseWebhookPayload("this is not json");

        // Assert
        result.ShouldBeNull();
    }

    [Test]
    public void VerifySubscription_With_Matching_Token_Returns_True()
    {
        // Act
        var result = _sut.VerifySubscription(ValidConfigJson, "verify-token");

        // Assert
        result.ShouldBeTrue();
    }

    [Test]
    public void VerifySubscription_With_Mismatched_Token_Returns_False()
    {
        // Act
        var result = _sut.VerifySubscription(ValidConfigJson, "wrong-token");

        // Assert
        result.ShouldBeFalse();
    }

    [Test]
    public void VerifySubscription_With_Missing_VerifyToken_Returns_False()
    {
        // Act
        var result = _sut.VerifySubscription("{}", "verify-token");

        // Assert
        result.ShouldBeFalse();
    }

    private static string ComputeSignature(string body, string appSecret)
    {
        var hash = HMACSHA256.HashData(Encoding.UTF8.GetBytes(appSecret), Encoding.UTF8.GetBytes(body));
        return "sha256=" + Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static WebhookValidationContext BuildWebhookContext(string body, string signatureHeaderValue)
    {
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            [SignatureHeaderName] = signatureHeaderValue
        };
        return new WebhookValidationContext(body, headers, ValidConfigJson, string.Empty);
    }

    private sealed class FakeHttpMessageHandler : HttpMessageHandler
    {
        public HttpResponseMessage Response { get; set; } = new(HttpStatusCode.OK);
        public HttpRequestMessage? LastRequest { get; private set; }
        public string? LastRequestBody { get; private set; }
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastRequest = request;
            LastRequestBody = request.Content != null
                ? await request.Content.ReadAsStringAsync(cancellationToken)
                : null;
            return Response;
        }
    }
}
