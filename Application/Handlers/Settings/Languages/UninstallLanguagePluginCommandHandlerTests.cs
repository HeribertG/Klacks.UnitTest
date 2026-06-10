// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for UninstallLanguagePluginCommandHandler: delegates to the language plugin service and
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
public class UninstallLanguagePluginCommandHandlerTests
{
    [Test]
    public async Task Handle_DelegatesToService()
    {
        var pluginService = Substitute.For<ILanguagePluginService>();
        pluginService.UninstallAsync("gsw").Returns(true);
        var handler = new UninstallLanguagePluginCommandHandler(pluginService);

        var result = await handler.Handle(new UninstallLanguagePluginCommand("gsw"), CancellationToken.None);

        result.ShouldBeTrue();
        await pluginService.Received(1).UninstallAsync("gsw");
    }

    [Test]
    public async Task Handle_CoreLanguage_ReturnsFalse()
    {
        var pluginService = Substitute.For<ILanguagePluginService>();
        pluginService.UninstallAsync("de").Returns(false);
        var handler = new UninstallLanguagePluginCommandHandler(pluginService);

        var result = await handler.Handle(new UninstallLanguagePluginCommand("de"), CancellationToken.None);

        result.ShouldBeFalse();
    }
}
