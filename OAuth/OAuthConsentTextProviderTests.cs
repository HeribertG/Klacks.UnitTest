// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Klacksy;
using Klacks.Api.Application.Services.OAuth;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.OAuth;

[TestFixture]
public class OAuthConsentTextProviderTests
{
    private IInstallationLanguageResolver _installationLanguage = null!;
    private OAuthConsentTextProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        OAuthConsentTexts.Reset();
        _installationLanguage = Substitute.For<IInstallationLanguageResolver>();
        _installationLanguage.ResolveAsync(Arg.Any<CancellationToken>()).Returns("en");
        _provider = new OAuthConsentTextProvider(_installationLanguage);
    }

    [TearDown]
    public void TearDown()
    {
        OAuthConsentTexts.Reset();
    }

    private static string Title(string language) => OAuthConsentTexts.VariantsOf(OAuthConsentTexts.Title)[language];

    [Test]
    public async Task GetAsync_FirstKnownLanguageOfTheVisitor_Wins()
    {
        var set = await _provider.GetAsync(["xx", "de-CH", "fr"]);

        set.LanguageCode.ShouldBe("de");
        set.Get(OAuthConsentTexts.Title).ShouldBe(Title("de"));
    }

    [Test]
    public async Task GetAsync_VisitorLanguageBeatsTheInstallationLanguage()
    {
        _installationLanguage.ResolveAsync(Arg.Any<CancellationToken>()).Returns("fr");

        var set = await _provider.GetAsync(["it"]);

        set.LanguageCode.ShouldBe("it");
        await _installationLanguage.DidNotReceive().ResolveAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetAsync_NoKnownVisitorLanguage_UsesTheInstallationLanguage()
    {
        _installationLanguage.ResolveAsync(Arg.Any<CancellationToken>()).Returns("fr");

        var set = await _provider.GetAsync(["xx", "yy-ZZ"]);

        set.LanguageCode.ShouldBe("fr");
        set.Get(OAuthConsentTexts.Title).ShouldBe(Title("fr"));
    }

    [Test]
    public async Task GetAsync_NoHeaderAtAll_UsesTheInstallationLanguage()
    {
        _installationLanguage.ResolveAsync(Arg.Any<CancellationToken>()).Returns("de-CH");

        var set = await _provider.GetAsync([]);

        set.LanguageCode.ShouldBe("de");
    }

    [Test]
    public async Task GetAsync_UnknownVisitorAndUnknownInstallationLanguage_IsEnglish()
    {
        _installationLanguage.ResolveAsync(Arg.Any<CancellationToken>()).Returns("xx");

        var set = await _provider.GetAsync(["yy"]);

        set.LanguageCode.ShouldBe("en");
        set.Get(OAuthConsentTexts.Title).ShouldBe(Title("en"));
    }

    [Test]
    public async Task GetAsync_PackNotLoaded_FallsBackInsteadOfSpeakingTheUnloadedLanguage()
    {
        var set = await _provider.GetAsync(["ar"]);

        set.LanguageCode.ShouldBe("en");
        set.IsRightToLeft.ShouldBeFalse();
    }

    [TestCase("ar", true)]
    [TestCase("he", true)]
    [TestCase("de", false)]
    [TestCase("zh-CN", false)]
    public async Task GetAsync_LoadedPack_IsRightToLeftOnlyForArabicAndHebrew(string language, bool rightToLeft)
    {
        AssistantTextsPluginLoader.Load(RepositoryRootLocator.ApiProject);

        var set = await _provider.GetAsync([language]);

        set.LanguageCode.ShouldBe(language);
        set.IsRightToLeft.ShouldBe(rightToLeft);
    }

    [Test]
    public async Task GetAsync_LoadedPack_ServesTheTextsOfThePack()
    {
        AssistantTextsPluginLoader.Load(RepositoryRootLocator.ApiProject);

        var set = await _provider.GetAsync(["ja"]);

        set.Get(OAuthConsentTexts.Title).ShouldContain("Klacks");
        set.Get(OAuthConsentTexts.Title).ShouldNotBe(Title("en"));
    }

    [Test]
    public async Task GetAsync_KeyMissingInAPack_IsTakenInEnglishNeverAsABareKey()
    {
        OAuthConsentTexts.Configure("qa", new Dictionary<string, string> { [OAuthConsentTexts.Title] = "QA title" });

        var set = await _provider.GetAsync(["qa"]);

        set.LanguageCode.ShouldBe("qa");
        set.Get(OAuthConsentTexts.Title).ShouldBe("QA title");
        set.Get(OAuthConsentTexts.Intro).ShouldBe(OAuthConsentTexts.EnglishOf(OAuthConsentTexts.Intro));
    }

    [Test]
    public async Task GetAsync_ReturnsEveryRequiredKey()
    {
        var set = await _provider.GetAsync(["de"]);

        set.Texts.Keys.OrderBy(k => k).ShouldBe(OAuthConsentTexts.RequiredKeys.OrderBy(k => k));
        set.Texts.Values.ShouldAllBe(text => !string.IsNullOrWhiteSpace(text));
    }
}
