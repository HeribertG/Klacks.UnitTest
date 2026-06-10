// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for uninstall_language_pack: the skill validates the pack code against
/// ListLanguagePluginsQuery (unknown, core, not installed) before sending
/// UninstallLanguagePluginCommand.
/// </summary>

using Klacks.Api.Application.Commands.Settings.Languages;
using Klacks.Api.Application.DTOs.Config;
using Klacks.Api.Application.Queries.Settings.Languages;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UninstallLanguagePackSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "Admin" }
    };

    private static IMediator MediatorWithPacks(params LanguagePluginInfo[] packs)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListLanguagePluginsQuery>(), Arg.Any<CancellationToken>())
            .Returns(packs.ToList());
        return mediator;
    }

    [Test]
    public async Task Uninstall_InstalledPack_SendsCommand()
    {
        var mediator = MediatorWithPacks(
            new LanguagePluginInfo { Code = "gsw", DisplayName = "Schwiizerdütsch", IsCore = false, IsInstalled = true });
        mediator.Send(Arg.Any<UninstallLanguagePluginCommand>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var skill = new UninstallLanguagePackSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["code"] = "gsw" });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("uninstalled");
        await mediator.Received(1).Send(
            Arg.Is<UninstallLanguagePluginCommand>(c => c.Code == "gsw"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Uninstall_MissingCode_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new UninstallLanguagePackSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Missing required parameter 'code'");
    }

    [Test]
    public async Task Uninstall_CoreLanguage_ReturnsError()
    {
        var mediator = MediatorWithPacks(
            new LanguagePluginInfo { Code = "de", DisplayName = "DE", IsCore = true, IsInstalled = true });
        var skill = new UninstallLanguagePackSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["code"] = "de" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("core language");
        await mediator.DidNotReceive().Send(
            Arg.Any<UninstallLanguagePluginCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Uninstall_NotInstalled_ReturnsError()
    {
        var mediator = MediatorWithPacks(
            new LanguagePluginInfo { Code = "rm", DisplayName = "Rumantsch", IsCore = false, IsInstalled = false });
        var skill = new UninstallLanguagePackSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["code"] = "rm" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not installed");
        await mediator.DidNotReceive().Send(
            Arg.Any<UninstallLanguagePluginCommand>(), Arg.Any<CancellationToken>());
    }
}
