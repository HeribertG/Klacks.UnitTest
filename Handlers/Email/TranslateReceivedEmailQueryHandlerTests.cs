// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Handlers.Email;
using Klacks.Api.Application.Queries.Email;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Interfaces.Translation;
using Klacks.Api.Domain.Models.Email;
using Microsoft.Extensions.Logging;
using NSubstitute.ExceptionExtensions;

namespace Klacks.UnitTest.Handlers.Email;

[TestFixture]
public class TranslateReceivedEmailQueryHandlerTests
{
    private const string TargetLanguage = "de";

    private IReceivedEmailRepository _repository = null!;
    private ITranslationService _translationService = null!;
    private TranslateReceivedEmailQueryHandler _handler = null!;
    private Guid _emailId;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IReceivedEmailRepository>();
        _translationService = Substitute.For<ITranslationService>();
        _emailId = Guid.NewGuid();

        var logger = Substitute.For<ILogger<TranslateReceivedEmailQueryHandler>>();
        _handler = new TranslateReceivedEmailQueryHandler(_repository, _translationService, logger);
    }

    [Test]
    public async Task Handle_EmailNotFound_ReturnsNull()
    {
        _repository.GetByIdAsync(_emailId).Returns((ReceivedEmail?)null);

        var result = await _handler.Handle(new TranslateReceivedEmailQuery(_emailId, TargetLanguage), CancellationToken.None);

        result.ShouldBeNull();
    }

    [Test]
    public async Task Handle_HtmlBodyPresent_TranslatesSubjectAndHtmlBody()
    {
        var email = new ReceivedEmail
        {
            Subject = "Hello",
            BodyHtml = "<p>Hello world</p>",
            BodyText = null,
        };
        _repository.GetByIdAsync(_emailId).Returns(email);

        _translationService
            .TranslateAsync("Hello", null, TargetLanguage, false)
            .Returns(new TranslationResult("Hallo", "en", TargetLanguage));
        _translationService
            .TranslateAsync("<p>Hello world</p>", null, TargetLanguage, true)
            .Returns(new TranslationResult("<p>Hallo Welt</p>", "en", TargetLanguage));

        var result = await _handler.Handle(new TranslateReceivedEmailQuery(_emailId, TargetLanguage), CancellationToken.None);

        result.ShouldNotBeNull();
        result.Subject.ShouldBe("Hallo");
        result.BodyHtml.ShouldBe("<p>Hallo Welt</p>");
        result.BodyText.ShouldBeNull();
        result.TargetLanguage.ShouldBe(TargetLanguage);
        await _translationService.Received(1).TranslateAsync("<p>Hello world</p>", null, TargetLanguage, true);
    }

    [Test]
    public async Task Handle_OnlyTextBodyPresent_TranslatesSubjectAndTextBody()
    {
        var email = new ReceivedEmail
        {
            Subject = "Hello",
            BodyHtml = null,
            BodyText = "Hello world",
        };
        _repository.GetByIdAsync(_emailId).Returns(email);

        _translationService
            .TranslateAsync("Hello", null, TargetLanguage, false)
            .Returns(new TranslationResult("Hallo", "en", TargetLanguage));
        _translationService
            .TranslateAsync("Hello world", null, TargetLanguage, false)
            .Returns(new TranslationResult("Hallo Welt", "en", TargetLanguage));

        var result = await _handler.Handle(new TranslateReceivedEmailQuery(_emailId, TargetLanguage), CancellationToken.None);

        result.ShouldNotBeNull();
        result.BodyHtml.ShouldBeNull();
        result.BodyText.ShouldBe("Hallo Welt");
        await _translationService.Received(1).TranslateAsync("Hello world", null, TargetLanguage, false);
    }

    [Test]
    public async Task Handle_TranslationServiceThrowsAuthenticationException_PropagatesUnchanged()
    {
        var email = new ReceivedEmail
        {
            Subject = "Hello",
            BodyHtml = "<p>Hello world</p>",
        };
        _repository.GetByIdAsync(_emailId).Returns(email);

        _translationService
            .TranslateAsync("Hello", null, TargetLanguage, false)
            .ThrowsAsync(new TranslationAuthenticationException("DeepL rejected the API key."));

        await Should.ThrowAsync<TranslationAuthenticationException>(
            () => _handler.Handle(new TranslateReceivedEmailQuery(_emailId, TargetLanguage), CancellationToken.None));
    }
}
