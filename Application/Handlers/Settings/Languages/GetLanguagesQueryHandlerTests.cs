// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for GetLanguagesQueryHandler: core defaults when configuration is empty, configured
/// overrides, and merging of installed plugin codes with their metadata.
/// </summary>
namespace Klacks.UnitTest.Application.Handlers.Settings.Languages;

using Klacks.Api.Application.DTOs.Config;
using Klacks.Api.Application.Handlers.Settings.Languages;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Application.Queries.Settings.Languages;
using Microsoft.Extensions.Configuration;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class GetLanguagesQueryHandlerTests
{
    private ILanguagePluginService _pluginService = null!;

    [SetUp]
    public void SetUp()
    {
        _pluginService = Substitute.For<ILanguagePluginService>();
        _pluginService.GetInstalledPluginCodes().Returns(new List<string>());
    }

    private static IConfiguration EmptyConfiguration()
    {
        return new ConfigurationBuilder().Build();
    }

    [Test]
    public async Task Handle_EmptyConfiguration_UsesCoreDefaults()
    {
        var handler = new GetLanguagesQueryHandler(EmptyConfiguration(), _pluginService);

        var result = await handler.Handle(new GetLanguagesQuery(), CancellationToken.None);

        result.SupportedLanguages.ShouldBe(Klacks.Api.Domain.Common.LanguageConfig.SupportedLanguages);
        result.FallbackOrder.ShouldBe(Klacks.Api.Domain.Common.LanguageConfig.FallbackOrder);
    }

    [Test]
    public async Task Handle_ConfiguredLanguages_OverrideDefaults()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Languages:Supported:0"] = "en",
                ["Languages:FallbackOrder:0"] = "en"
            })
            .Build();
        var handler = new GetLanguagesQueryHandler(configuration, _pluginService);

        var result = await handler.Handle(new GetLanguagesQuery(), CancellationToken.None);

        result.SupportedLanguages.ShouldBe(new[] { "en" });
        result.FallbackOrder.ShouldBe(new[] { "en" });
    }

    [Test]
    public async Task Handle_InstalledPlugin_AppendsCodeAndMetadata()
    {
        _pluginService.GetInstalledPluginCodes().Returns(new List<string> { "gsw" });
        _pluginService.GetPlugin("gsw").Returns(new LanguagePluginInfo
        {
            Code = "gsw",
            Name = "gsw",
            DisplayName = "Schwiizerdütsch",
            SpeechLocale = "de-CH",
            Direction = "ltr"
        });
        var handler = new GetLanguagesQueryHandler(EmptyConfiguration(), _pluginService);

        var result = await handler.Handle(new GetLanguagesQuery(), CancellationToken.None);

        result.SupportedLanguages.ShouldContain("gsw");
        result.Metadata.ShouldContainKey("gsw");
        result.Metadata["gsw"].DisplayName.ShouldBe("Schwiizerdütsch");
        result.Metadata["gsw"].SpeechLocale.ShouldBe("de-CH");
    }
}
