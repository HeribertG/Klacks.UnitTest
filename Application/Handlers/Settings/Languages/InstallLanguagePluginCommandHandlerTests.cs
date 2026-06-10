// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for InstallLanguagePluginCommandHandler: delegates to the language plugin service and
/// passes through its result.
/// </summary>
namespace Klacks.UnitTest.Application.Handlers.Settings.Languages;

using Klacks.Api.Application.Commands.Settings.Languages;
using Klacks.Api.Application.Handlers.Settings.Languages;
using Klacks.Api.Application.Interfaces.Settings;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class InstallLanguagePluginCommandHandlerTests
{
    [Test]
    public async Task Handle_DelegatesToService()
    {
        var pluginService = Substitute.For<ILanguagePluginService>();
        pluginService.InstallAsync("gsw").Returns(true);
        var handler = new InstallLanguagePluginCommandHandler(pluginService);

        var result = await handler.Handle(new InstallLanguagePluginCommand("gsw"), CancellationToken.None);

        result.ShouldBeTrue();
        await pluginService.Received(1).InstallAsync("gsw");
    }

    [Test]
    public async Task Handle_ServiceRejects_ReturnsFalse()
    {
        var pluginService = Substitute.For<ILanguagePluginService>();
        pluginService.InstallAsync("de").Returns(false);
        var handler = new InstallLanguagePluginCommandHandler(pluginService);

        var result = await handler.Handle(new InstallLanguagePluginCommand("de"), CancellationToken.None);

        result.ShouldBeFalse();
    }
}
