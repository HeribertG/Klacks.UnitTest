// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Klacks.Api.Application.Helpers;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Helpers;

[TestFixture]
public class LocalizedCommentParamsTests
{
    private static MultiLanguage Christmas()
    {
        var name = new MultiLanguage { De = "Weihnachten", En = "Christmas Day", Fr = "Noël", It = "Natale" };
        name.SetValue("ja", "クリスマス");
        name.SetValue("zh-CN", "圣诞节");
        return name;
    }

    [Test]
    public void Add_StoresReadableTextAndTheWholeMultiLanguage()
    {
        var parameters = new Dictionary<string, string>();

        LocalizedCommentParams.Add(parameters, LocalizedCommentParamKeys.Holiday, Christmas());

        parameters[LocalizedCommentParamKeys.Holiday].ShouldBe("Weihnachten");
        var names = JsonSerializer.Deserialize<Dictionary<string, string>>(parameters[LocalizedCommentParamKeys.HolidayMultiLanguage])!;
        names.Keys.ShouldBe(["de", "en", "fr", "it", "ja", "zh-cn"], ignoreOrder: true);
    }

    [TestCase("ja", "クリスマス")]
    [TestCase("zh-CN", "圣诞节")]
    [TestCase("zh-cn", "圣诞节")]
    [TestCase("fr", "Noël")]
    public void ForLanguage_ResolvesTheNameForTheReader(string language, string expected)
    {
        var parameters = new Dictionary<string, string> { ["minutes"] = "30" };
        LocalizedCommentParams.Add(parameters, LocalizedCommentParamKeys.Holiday, Christmas());

        var resolved = LocalizedCommentParams.ForLanguage(parameters, language);

        resolved[LocalizedCommentParamKeys.Holiday].ShouldBe(expected);
        resolved["minutes"].ShouldBe("30");
        resolved.ShouldNotContainKey(LocalizedCommentParamKeys.HolidayMultiLanguage);
    }

    [Test]
    public void ForLanguage_LanguageWithoutName_FallsBackToTheFirstCoreLanguage()
    {
        var parameters = new Dictionary<string, string>();
        LocalizedCommentParams.Add(parameters, LocalizedCommentParamKeys.Holiday, Christmas());

        LocalizedCommentParams.ForLanguage(parameters, "th")[LocalizedCommentParamKeys.Holiday].ShouldBe("Weihnachten");
    }

    [Test]
    public void ForLanguage_MalformedCompanion_KeepsThePlainText()
    {
        var parameters = new Dictionary<string, string>
        {
            [LocalizedCommentParamKeys.Holiday] = "Weihnachten",
            [LocalizedCommentParamKeys.HolidayMultiLanguage] = "not json",
        };

        var resolved = LocalizedCommentParams.ForLanguage(parameters, "ja");

        resolved[LocalizedCommentParamKeys.Holiday].ShouldBe("Weihnachten");
        resolved.ShouldNotContainKey(LocalizedCommentParamKeys.HolidayMultiLanguage);
    }
}
