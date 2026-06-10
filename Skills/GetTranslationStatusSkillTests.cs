// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for get_translation_status: the skill sends GetTranslationStatusQuery and reports
/// whether DeepL is configured, pointing to update_deepl_settings when it is not.
/// </summary>

using Klacks.Api.Application.Queries.Settings.Languages;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetTranslationStatusSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewSettings" }
    };

    [Test]
    public async Task Status_Configured_ReportsReady()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetTranslationStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var skill = new GetTranslationStatusSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("configured");
        result.Message.ShouldNotContain("update_deepl_settings");
        await mediator.Received(1).Send(Arg.Any<GetTranslationStatusQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Status_NotConfigured_PointsToDeeplSettings()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetTranslationStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var skill = new GetTranslationStatusSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("not configured");
        result.Message.ShouldContain("update_deepl_settings");
    }
}
