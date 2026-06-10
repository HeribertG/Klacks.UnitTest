// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ListLanguagePluginsQueryHandler: returns the plugin list from the language plugin service.
/// </summary>
namespace Klacks.UnitTest.Application.Handlers.Settings.Languages;

using Klacks.Api.Application.DTOs.Config;
using Klacks.Api.Application.Handlers.Settings.Languages;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Application.Queries.Settings.Languages;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class ListLanguagePluginsQueryHandlerTests
{
    [Test]
    public async Task Handle_ReturnsPluginsFromService()
    {
        var pluginService = Substitute.For<ILanguagePluginService>();
        pluginService.GetAllPlugins().Returns(new List<LanguagePluginInfo>
        {
            new() { Code = "de", IsCore = true, IsInstalled = true },
            new() { Code = "gsw", IsCore = false, IsInstalled = false }
        });
        var handler = new ListLanguagePluginsQueryHandler(pluginService);

        var result = await handler.Handle(new ListLanguagePluginsQuery(), CancellationToken.None);

        result.Count.ShouldBe(2);
        result.ShouldContain(p => p.Code == "gsw" && !p.IsCore);
    }
}
