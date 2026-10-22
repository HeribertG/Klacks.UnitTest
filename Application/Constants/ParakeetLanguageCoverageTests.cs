// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for ParakeetLanguageCoverage: the 25 covered base languages, region suffixes and casing ignored,
/// blank counts as covered, uncovered interface languages are not.
/// </summary>

using Klacks.Api.Application.Constants;

namespace Klacks.UnitTest.Application.Constants;

[TestFixture]
public class ParakeetLanguageCoverageTests
{
    [Test]
    public void Languages_AreTheTwentyFiveParakeetLanguages_WithoutDuplicates()
    {
        ParakeetLanguageCoverage.Languages.Count.ShouldBe(25);
        ParakeetLanguageCoverage.Languages.Distinct().Count().ShouldBe(25);
    }

    [TestCase("bg")]
    [TestCase("de")]
    [TestCase("de-CH")]
    [TestCase("EN-gb")]
    [TestCase(" fr ")]
    [TestCase("uk")]
    [TestCase("")]
    [TestCase(null)]
    public void IsCovered_IsTrueForCoveredOrBlankLanguages(string? language)
    {
        ParakeetLanguageCoverage.IsCovered(language).ShouldBeTrue();
    }

    [TestCase("ar")]
    [TestCase("he")]
    [TestCase("id")]
    [TestCase("ja")]
    [TestCase("ko")]
    [TestCase("ms")]
    [TestCase("nb")]
    [TestCase("th")]
    [TestCase("vi")]
    [TestCase("zh-CN")]
    [TestCase("zh-TW")]
    public void IsCovered_IsFalseForUncoveredInterfaceLanguages(string language)
    {
        ParakeetLanguageCoverage.IsCovered(language).ShouldBeFalse();
    }
}
