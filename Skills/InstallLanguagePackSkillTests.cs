// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for install_language_pack: the skill validates the pack code against
/// ListLanguagePluginsQuery (unknown, core, already installed) before sending
/// InstallLanguagePluginCommand.
/// </summary>

using Klacks.Api.Application.Commands.Settings.Languages;
using Klacks.Api.Application.DTOs.Config;
using Klacks.Api.Application.Queries.Settings.Languages;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class InstallLanguagePackSkillTests
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
    public async Task Install_AvailablePack_SendsCommand()
    {
        var mediator = MediatorWithPacks(
            new LanguagePluginInfo { Code = "gsw", DisplayName = "Schwiizerdütsch", IsCore = false, IsInstalled = false });
        mediator.Send(Arg.Any<InstallLanguagePluginCommand>(), Arg.Any<CancellationToken>())
            .Returns(true);
        var skill = new InstallLanguagePackSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["code"] = "GSW" });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("installed and activated");
        await mediator.Received(1).Send(
            Arg.Is<InstallLanguagePluginCommand>(c => c.Code == "gsw"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Install_MissingCode_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new InstallLanguagePackSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Missing required parameter 'code'");
    }

    [Test]
    public async Task Install_UnknownPack_ReturnsError()
    {
        var mediator = MediatorWithPacks();
        var skill = new InstallLanguagePackSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["code"] = "xx" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("unknown");
        await mediator.DidNotReceive().Send(
            Arg.Any<InstallLanguagePluginCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Install_CoreLanguage_ReturnsError()
    {
        var mediator = MediatorWithPacks(
            new LanguagePluginInfo { Code = "de", DisplayName = "DE", IsCore = true, IsInstalled = true });
        var skill = new InstallLanguagePackSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["code"] = "de" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("core language");
        await mediator.DidNotReceive().Send(
            Arg.Any<InstallLanguagePluginCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Install_AlreadyInstalled_ReturnsSuccessWithoutCommand()
    {
        var mediator = MediatorWithPacks(
            new LanguagePluginInfo { Code = "gsw", DisplayName = "Schwiizerdütsch", IsCore = false, IsInstalled = true });
        var skill = new InstallLanguagePackSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["code"] = "gsw" });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("already installed");
        await mediator.DidNotReceive().Send(
            Arg.Any<InstallLanguagePluginCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Install_CommandFails_ReturnsVersionError()
    {
        var mediator = MediatorWithPacks(
            new LanguagePluginInfo { Code = "gsw", DisplayName = "Schwiizerdütsch", IsCore = false, IsInstalled = false, MinKlacksVersion = "9.9.9" });
        mediator.Send(Arg.Any<InstallLanguagePluginCommand>(), Arg.Any<CancellationToken>())
            .Returns(false);
        var skill = new InstallLanguagePackSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["code"] = "gsw" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("9.9.9");
    }
}
