// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests that the web search API key is treated as a secret end to end: the settings list hands out
/// the placeholder instead of the key, and the provider factory still receives the readable key
/// because it decrypts what the store holds. Both run against the real SettingsEncryptionService, so
/// removing the key from its type lists turns these red.
/// </summary>

using System.Net;
using System.Net.Http.Json;
using Klacks.Api.Application.Handlers.Settings.Setting;
using Klacks.Api.Application.Queries.Settings.Settings;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Settings;
using Klacks.Api.Infrastructure.WebSearch;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using SettingsConstants = Klacks.Api.Application.Constants.Settings;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Services.Settings;

[TestFixture]
public class WebSearchApiKeyProtectionTests
{
    private const string PlainApiKey = "serper-live-key-8f3a91";
    private const string ApiKeyHeader = "X-API-KEY";

    private SettingsEncryptionService _encryptionService = null!;
    private ISettingsRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        _encryptionService = new SettingsEncryptionService(
            new StubProtectionProvider(),
            Substitute.For<ILogger<SettingsEncryptionService>>());

        _repository = Substitute.For<ISettingsRepository>();
    }

    [Test]
    public async Task SettingsList_NeverHandsOutTheWebSearchApiKey()
    {
        var stored = _encryptionService.ProcessForStorage(SettingsConstants.WEB_SEARCH_API_KEY, PlainApiKey);

        stored.ShouldStartWith("ENC:");
        stored.ShouldNotContain(PlainApiKey);

        _repository.GetSettingsList().Returns(new List<SettingsModel>
        {
            new() { Type = SettingsConstants.WEB_SEARCH_API_KEY, Value = stored }
        });

        var handler = new ListQueryHandler(
            _repository, _encryptionService, Substitute.For<ILogger<ListQueryHandler>>());

        var result = await handler.Handle(new ListQuery(), CancellationToken.None);

        var value = result.Single().Value;

        value.ShouldBe(SettingsMasking.MaskedValue,
            "the web search key must be masked like every other secret setting");
        value.ShouldNotContain(PlainApiKey);
    }

    [Test]
    public async Task ProviderFactory_SendsTheDecryptedKey_NotTheStoredCipherText()
    {
        var stored = _encryptionService.ProcessForStorage(SettingsConstants.WEB_SEARCH_API_KEY, PlainApiKey);

        _repository.GetSetting(SettingsConstants.WEB_SEARCH_PROVIDER)
            .Returns(new SettingsModel { Type = SettingsConstants.WEB_SEARCH_PROVIDER, Value = "serper" });
        _repository.GetSetting(SettingsConstants.WEB_SEARCH_API_KEY)
            .Returns(new SettingsModel { Type = SettingsConstants.WEB_SEARCH_API_KEY, Value = stored });

        var handler = new CapturingHandler();
        var httpClientFactory = Substitute.For<IHttpClientFactory>();
        httpClientFactory.CreateClient().Returns(_ => new HttpClient(handler, disposeHandler: false));

        var factory = new WebSearchProviderFactory(_repository, httpClientFactory, _encryptionService);

        var provider = await factory.CreateAsync();

        provider.ShouldNotBeNull();

        await provider!.SearchAsync("klacks");

        handler.SentApiKey.ShouldBe(PlainApiKey,
            "the provider must be given the readable key, not the stored cipher text");
        handler.SentApiKey.ShouldNotStartWith("ENC:");
    }

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public string? SentApiKey { get; private set; }

        protected override Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            if (request.Headers.TryGetValues(ApiKeyHeader, out var values))
            {
                SentApiKey = values.FirstOrDefault();
            }

            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = JsonContent.Create(new { organic = Array.Empty<object>() })
            });
        }
    }

    private sealed class StubProtectionProvider : IDataProtectionProvider, IDataProtector
    {
        private const byte Mask = 0x5A;

        public IDataProtector CreateProtector(string purpose) => this;

        public byte[] Protect(byte[] plaintext) => Transform(plaintext);

        public byte[] Unprotect(byte[] protectedData) => Transform(protectedData);

        private static byte[] Transform(byte[] data)
        {
            var result = new byte[data.Length];
            for (var i = 0; i < data.Length; i++)
            {
                result[i] = (byte)(data[i] ^ Mask);
            }

            return result;
        }
    }
}
