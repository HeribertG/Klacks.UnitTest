// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for SttSpeechTranscriptionService: a custom provider id is resolved through the custom
/// provider resolver and transcribed through the custom session factory without an API key, an unknown
/// custom id keeps the existing "Unknown STT provider" error, and built-in providers still need their key.
/// </summary>

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.SpeechEval;

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant.Evaluation.SpeechEval;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Shouldly;

[TestFixture]
public class SttSpeechTranscriptionServiceTests
{
    private const string Language = "de";
    private const string Transcript = "hallo welt";

    private static readonly byte[] Audio = [1, 2, 3];

    private ISttProvider _groqProvider = null!;
    private ISettingsReader _settingsReader = null!;
    private ICustomSttProviderResolver _resolver = null!;
    private ICustomSttSessionFactory _sessionFactory = null!;
    private ISttSession _session = null!;
    private SttSpeechTranscriptionService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _groqProvider = Substitute.For<ISttProvider>();
        _groqProvider.ProviderId.Returns(SttProviderConstants.GroqWhisper);
        _settingsReader = Substitute.For<ISettingsReader>();
        _resolver = Substitute.For<ICustomSttProviderResolver>();
        _resolver.IsCustomProviderId(Arg.Any<string>())
            .Returns(call => call.Arg<string>().StartsWith(SttProviderConstants.CustomProviderPrefix, StringComparison.OrdinalIgnoreCase));
        _session = Substitute.For<ISttSession>();
        _sessionFactory = Substitute.For<ICustomSttSessionFactory>();
        _sessionFactory.CreateSession(Arg.Any<CustomSttProvider>(), Arg.Any<string>()).Returns(_session);

        _service = new SttSpeechTranscriptionService(
            [_groqProvider],
            _settingsReader,
            Substitute.For<ISettingsEncryptionService>(),
            _resolver,
            _sessionFactory);
    }

    [TearDown]
    public async Task TearDown()
    {
        await _session.DisposeAsync();
    }

    [Test]
    public async Task TranscribeAsync_RoutesCustomProviderIdThroughTheSessionFactory_WithoutApiKey()
    {
        var provider = new CustomSttProvider { Id = WhisperPluginConstants.ProviderId, IsEnabled = true };
        var providerId = $"{SttProviderConstants.CustomProviderPrefix}{provider.Id}";
        _resolver.TryResolveAsync(providerId, Arg.Any<CancellationToken>()).Returns(provider);
        _session.ReceiveAsync(Arg.Any<CancellationToken>()).Returns(new SttResult(Transcript, true, 1.0f));

        var text = await _service.TranscribeAsync(providerId, Audio, Language);

        text.ShouldBe(Transcript);
        _sessionFactory.Received(1).CreateSession(provider, Language);
        await _session.Received(1).SendAudioAsync(Audio, Arg.Any<CancellationToken>());
        await _settingsReader.DidNotReceiveWithAnyArgs().GetSetting(default!);
    }

    [Test]
    public async Task TranscribeAsync_ReturnsEmptyText_WhenTheCustomEngineHeardNothing()
    {
        var provider = new CustomSttProvider { Id = Guid.NewGuid(), IsEnabled = true };
        var providerId = $"{SttProviderConstants.CustomProviderPrefix}{provider.Id}";
        _resolver.TryResolveAsync(providerId, Arg.Any<CancellationToken>()).Returns(provider);
        _session.ReceiveAsync(Arg.Any<CancellationToken>()).Returns((SttResult?)null);

        (await _service.TranscribeAsync(providerId, Audio, Language)).ShouldBe(string.Empty);
    }

    [Test]
    public async Task TranscribeAsync_ThrowsUnknownProvider_WhenTheCustomIdDoesNotResolve()
    {
        var providerId = $"{SttProviderConstants.CustomProviderPrefix}{Guid.NewGuid()}";
        _resolver.TryResolveAsync(providerId, Arg.Any<CancellationToken>()).Returns((CustomSttProvider?)null);

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _service.TranscribeAsync(providerId, Audio, Language));

        ex.Message.ShouldBe($"Unknown STT provider: {providerId}");
        _sessionFactory.DidNotReceiveWithAnyArgs().CreateSession(default!, default!);
    }

    [Test]
    public async Task TranscribeAsync_StillRequiresAnApiKey_ForBuiltInProviders()
    {
        _settingsReader.GetSetting(Arg.Any<string>()).Returns((Klacks.Api.Domain.Models.Settings.Settings?)null);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.TranscribeAsync(SttProviderConstants.GroqWhisper, Audio, Language));

        ex.Message.ShouldContain("No API key configured");
    }

    [Test]
    public async Task TranscribeAsync_ThrowsUnknownProvider_ForAnUnregisteredBuiltInId()
    {
        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => _service.TranscribeAsync("nonexistent", Audio, Language));

        ex.Message.ShouldBe("Unknown STT provider: nonexistent");
    }
}
