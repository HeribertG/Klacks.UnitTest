// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for list_languages: the skill sends GetLanguagesQuery and ListLanguagePluginsQuery,
/// projects active languages plus pack details and reports the installed optional pack count.
/// </summary>

using Klacks.Api.Application.DTOs.Config;
using Klacks.Api.Application.Queries.Settings.Languages;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListLanguagesSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewSettings" }
    };

    [Test]
    public async Task List_ReturnsActiveLanguagesAndPacks()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetLanguagesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new LanguageConfigResponse
            {
                SupportedLanguages = ["de", "en", "fr", "it", "gsw"],
                FallbackOrder = ["de", "fr", "it", "en"]
            });
        mediator.Send(Arg.Any<ListLanguagePluginsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<LanguagePluginInfo>
            {
                new() { Code = "de", DisplayName = "DE", IsCore = true, IsInstalled = true },
                new() { Code = "gsw", DisplayName = "Schwiizerdütsch", IsCore = false, IsInstalled = true, TranslationCount = 1200 },
                new() { Code = "rm", DisplayName = "Rumantsch", IsCore = false, IsInstalled = false }
            });
        var skill = new ListLanguagesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("de, en, fr, it, gsw");
        result.Message.ShouldContain("1 of 2 optional language packs installed");
        await mediator.Received(1).Send(Arg.Any<GetLanguagesQuery>(), Arg.Any<CancellationToken>());
        await mediator.Received(1).Send(Arg.Any<ListLanguagePluginsQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_NoOptionalPacks_ReturnsZeroCount()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetLanguagesQuery>(), Arg.Any<CancellationToken>())
            .Returns(new LanguageConfigResponse
            {
                SupportedLanguages = ["de", "en", "fr", "it"],
                FallbackOrder = ["de", "fr", "it", "en"]
            });
        mediator.Send(Arg.Any<ListLanguagePluginsQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<LanguagePluginInfo>
            {
                new() { Code = "de", DisplayName = "DE", IsCore = true, IsInstalled = true }
            });
        var skill = new ListLanguagesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("0 of 0 optional language packs installed");
    }
}
