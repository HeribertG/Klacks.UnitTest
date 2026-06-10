// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for GetTranslationStatusQueryHandler: passes through the configuration state of the
/// translation service.
/// </summary>
namespace Klacks.UnitTest.Application.Handlers.Settings.Languages;

using Klacks.Api.Application.Handlers.Settings.Languages;
using Klacks.Api.Application.Queries.Settings.Languages;
using Klacks.Api.Domain.Interfaces.Translation;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class GetTranslationStatusQueryHandlerTests
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task Handle_ReturnsServiceConfigurationState(bool configured)
    {
        var translationService = Substitute.For<ITranslationService>();
        translationService.IsConfiguredAsync().Returns(configured);
        var handler = new GetTranslationStatusQueryHandler(translationService);

        var result = await handler.Handle(new GetTranslationStatusQuery(), CancellationToken.None);

        result.ShouldBe(configured);
    }
}
