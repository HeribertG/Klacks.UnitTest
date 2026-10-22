// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for SttController.Transcribe with a self-hosted provider: a language the local engine does not
/// support is answered with 400 carrying the stable unsupported_language code, other custom-engine failures
/// stay a plain 400 without that code, and a successful transcription returns the text.
/// </summary>

namespace Klacks.UnitTest.Controllers.Assistant;

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute.ExceptionExtensions;
using Shouldly;

[TestFixture]
public class SttControllerTranscribeTests
{
    private const string ErrorProperty = "error";
    private const string CodeProperty = "code";
    private const string TextProperty = "text";
    private const string Locale = "ja";

    private ICustomSttProviderRepository _providerRepository = null!;
    private ICustomSttSessionFactory _sessionFactory = null!;
    private ISttSession _session = null!;
    private SttController _controller = null!;

    [SetUp]
    public void SetUp()
    {
        var settingsRepository = Substitute.For<ISettingsRepository>();
        var engineSetting = new Klacks.Api.Domain.Models.Settings.Settings
        {
            Type = Klacks.Api.Application.Constants.Settings.ASSISTANT_STT_ENGINE,
            Value = $"{SttProviderConstants.CustomProviderPrefix}{WhisperPluginConstants.ProviderId}",
        };
        settingsRepository.GetSetting(Klacks.Api.Application.Constants.Settings.ASSISTANT_STT_ENGINE).Returns(engineSetting);

        _providerRepository = Substitute.For<ICustomSttProviderRepository>();
        _providerRepository.GetByIdAsync(WhisperPluginConstants.ProviderId, Arg.Any<CancellationToken>())
            .Returns(new CustomSttProvider
            {
                Id = WhisperPluginConstants.ProviderId,
                Name = WhisperPluginConstants.ParakeetProviderName,
                ApiUrl = "http://klacks-stt:8000",
                LanguageModel = WhisperPluginConstants.ParakeetModelId,
                IsEnabled = true,
            });

        _session = Substitute.For<ISttSession>();
        _sessionFactory = Substitute.For<ICustomSttSessionFactory>();
        _sessionFactory.CreateSession(Arg.Any<CustomSttProvider>(), Arg.Any<string>()).Returns(_session);

        _controller = new SttController(
            [],
            settingsRepository,
            _providerRepository,
            new CustomSttProviderResolver(_providerRepository),
            _sessionFactory,
            Substitute.For<ISettingsEncryptionService>(),
            NullLogger<SttController>.Instance)
        {
            ControllerContext = new ControllerContext { HttpContext = CreateHttpContext() },
        };
    }

    [TearDown]
    public async Task TearDown()
    {
        await _session.DisposeAsync();
    }

    private static DefaultHttpContext CreateHttpContext()
    {
        var context = new DefaultHttpContext();
        context.Request.Body = new MemoryStream([1, 2, 3]);
        return context;
    }

    private static object? ReadProperty(object? value, string name)
        => value?.GetType().GetProperty(name)?.GetValue(value);

    [Test]
    public async Task Transcribe_AnswersBadRequestWithUnsupportedLanguageCode_WhenEngineRejectsTheLanguage()
    {
        _session.ReceiveAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new UnsupportedSttLanguageException(Locale));

        var result = await _controller.Transcribe(Locale, CancellationToken.None);

        var badRequest = result.ShouldBeOfType<BadRequestObjectResult>();
        ReadProperty(badRequest.Value, CodeProperty).ShouldBe(SttProviderConstants.ErrorCodeUnsupportedLanguage);
        ReadProperty(badRequest.Value, ErrorProperty).ShouldBeOfType<string>().ShouldContain(Locale);
    }

    [Test]
    public async Task Transcribe_AnswersPlainBadRequestWithoutCode_ForOtherEngineFailures()
    {
        _session.ReceiveAsync(Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("Custom STT request failed (503)"));

        var result = await _controller.Transcribe(Locale, CancellationToken.None);

        var badRequest = result.ShouldBeOfType<BadRequestObjectResult>();
        ReadProperty(badRequest.Value, CodeProperty).ShouldBeNull();
        ReadProperty(badRequest.Value, ErrorProperty).ShouldBeOfType<string>().ShouldContain("503");
    }

    [Test]
    public async Task Transcribe_ReturnsTheText_WhenEngineSucceeds()
    {
        _session.ReceiveAsync(Arg.Any<CancellationToken>()).Returns(new SttResult("hallo welt", true, 1.0f));

        var result = await _controller.Transcribe("de", CancellationToken.None);

        var ok = result.ShouldBeOfType<OkObjectResult>();
        ReadProperty(ok.Value, TextProperty).ShouldBe("hallo welt");
    }
}
