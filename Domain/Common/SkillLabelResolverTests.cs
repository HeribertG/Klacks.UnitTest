// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The lookup that decides in which language a user-facing skill label is shown. Two rules carry the
/// whole class: the full tag wins over the base language, and there is no English fallback at all. The
/// first is why zh-CN and zh-TW can never read each other's labels, the second is why a language without
/// an authored label produces no question rather than an English one.
/// </summary>

using Klacks.Api.Domain.Common;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Common;

[TestFixture]
public class SkillLabelResolverTests
{
    private static readonly Dictionary<string, string> Labels = new(StringComparer.OrdinalIgnoreCase)
    {
        ["de"] = "Gruppe nach Regel füllen",
        ["en"] = "Fill the group from a rule",
        ["zh-CN"] = "按规则填充组",
        ["zh-TW"] = "依規則填滿群組"
    };

    [Test]
    public void AnExactTag_Wins()
    {
        SkillLabelResolver.Resolve(Labels, "zh-TW").ShouldBe("依規則填滿群組");
        SkillLabelResolver.Resolve(Labels, "zh-CN").ShouldBe("按规则填充组");
    }

    // The two Chinese scripts are the only regional pack codes Klacks ships. Stripping the region before
    // the lookup would let whichever pack was installed last answer for both.
    [Test]
    public void TheTwoChineseScripts_NeverShareALabel()
    {
        SkillLabelResolver.Resolve(Labels, "zh-TW")
            .ShouldNotBe(SkillLabelResolver.Resolve(Labels, "zh-CN"));
    }

    // A regional tag of a core language still finds the core entry, which is what makes de-CH work.
    [Test]
    public void ARegionalTagOfACoreLanguage_FallsBackToTheBaseLanguage()
    {
        SkillLabelResolver.Resolve(Labels, "de-CH").ShouldBe("Gruppe nach Regel füllen");
    }

    // A base tag never silently picks one of the regional variants: "zh" is not zh-CN.
    [Test]
    public void ABareBaseTagWithoutItsOwnEntry_ResolvesToNothing()
    {
        SkillLabelResolver.Resolve(Labels, "zh").ShouldBeNull();
    }

    [Test]
    public void TheLookupIsCaseInsensitive()
    {
        SkillLabelResolver.Resolve(Labels, "ZH-tw").ShouldBe("依規則填滿群組");
        SkillLabelResolver.Resolve(Labels, "DE").ShouldBe("Gruppe nach Regel füllen");
    }

    // Rule 4: an unauthored language gets NO label, never the English one. The caller then asks nothing.
    [Test]
    public void AnUnauthoredLanguage_NeverFallsBackToEnglish()
    {
        SkillLabelResolver.Resolve(Labels, "pl").ShouldBeNull();
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void WithoutALanguage_ResolvesToNothing(string? language)
    {
        SkillLabelResolver.Resolve(Labels, language).ShouldBeNull();
    }

    [Test]
    public void WithoutAnyLabels_ResolvesToNothing()
    {
        SkillLabelResolver.Resolve(null, "de").ShouldBeNull();
        SkillLabelResolver.Resolve(new Dictionary<string, string>(), "de").ShouldBeNull();
    }

    [Test]
    public void ABlankLabel_CountsAsAbsent()
    {
        var labels = new Dictionary<string, string> { ["de"] = "   " };

        SkillLabelResolver.Resolve(labels, "de").ShouldBeNull();
    }

    [Test]
    public void TheResolvedLabel_IsTrimmed()
    {
        var labels = new Dictionary<string, string> { ["de"] = "  Gruppe füllen  " };

        SkillLabelResolver.Resolve(labels, "de").ShouldBe("Gruppe füllen");
    }
}
