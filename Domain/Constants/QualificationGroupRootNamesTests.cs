// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the localized name of the qualifications root group: every shipped language (the four core languages
/// plus every language pack directory) has its own entry, regional tags resolve exactly first and then by base
/// language, and only an unknown language falls back to English.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Domain.Constants;

[TestFixture]
public class QualificationGroupRootNamesTests
{

    [Test]
    public void EveryShippedLanguage_HasItsOwnRootName()
    {
        var packLanguages = Directory.GetDirectories(Path.Combine(ApiDirectory(), LanguagePluginConstants.PluginDirectory))
            .Select(Path.GetFileName)
            .Select(name => name!);
        var shipped = MultiLanguage.CoreLanguages.Concat(packLanguages).ToList();

        shipped.Where(language => !QualificationGroupRootNames.ByLanguage.ContainsKey(language)).ShouldBeEmpty();
        QualificationGroupRootNames.ByLanguage.Values.ShouldAllBe(name => !string.IsNullOrWhiteSpace(name));
    }

    [TestCase("de", "Qualifikationen")]
    [TestCase("de-CH", "Qualifikationen")]
    [TestCase("fr", "Qualifications")]
    [TestCase("it", "Qualifiche")]
    [TestCase("zh-CN", "资质")]
    [TestCase("zh-TW", "資格")]
    [TestCase("ZH-tw", "資格")]
    [TestCase("pt-BR", "Qualificações")]
    [TestCase("xx", "Qualifications")]
    [TestCase("", "Qualifications")]
    [TestCase(null, "Qualifications")]
    public void Resolve_ExactThenBaseLanguage_EnglishOnlyForUnknown(string? language, string expected)
    {
        QualificationGroupRootNames.Resolve(language).ShouldBe(expected);
    }

    private static string ApiDirectory()
    {
        return RepositoryRootLocator.ApiProject;
    }
}
