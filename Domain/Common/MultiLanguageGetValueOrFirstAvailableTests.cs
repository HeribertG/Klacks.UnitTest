// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Domain.Common;

[TestFixture]
public class MultiLanguageGetValueOrFirstAvailableTests
{
    [Test]
    public void RequestedLanguage_IsReturnedInAnyCasing()
    {
        var name = new MultiLanguage { De = "Weihnachten", En = "Christmas Day" };
        name.SetValue("zh-CN", "圣诞节");

        name.GetValueOrFirstAvailable("zh-CN").ShouldBe("圣诞节");
        name.GetValueOrFirstAvailable("ZH-cn").ShouldBe("圣诞节");
    }

    [Test]
    public void MissingLanguage_FallsBackToCoreLanguagesInOrder()
    {
        new MultiLanguage { En = "Christmas Day", Fr = "Noël" }.GetValueOrFirstAvailable("ja").ShouldBe("Christmas Day");
        new MultiLanguage { De = "Weihnachten", En = "Christmas Day" }.GetValueOrFirstAvailable(null).ShouldBe("Weihnachten");
    }

    [Test]
    public void OnlyAPluginLanguage_IsStillReturned()
    {
        var name = new MultiLanguage();
        name.SetValue("ja", "クリスマス");

        name.GetValueOrFirstAvailable("de").ShouldBe("クリスマス");
    }

    [Test]
    public void EmptyName_YieldsAnEmptyString()
    {
        new MultiLanguage().GetValueOrFirstAvailable("de").ShouldBe(string.Empty);
    }
}
