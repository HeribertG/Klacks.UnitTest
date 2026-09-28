// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the id format of translated goldset items, i18n-&lt;locale&gt;--&lt;sourceId&gt;. The double hyphen is the
/// only separator, so hyphenated locales (zh-CN) and hyphenated source ids (w05-recipe-...) both parse back
/// without a list of known locales.
/// </summary>
namespace Klacks.UnitTest.Domain.Services.Assistant;

using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class GoldsetTranslationIdTests
{
    [Test]
    public void Compose_WritesPrefixLocaleSeparatorAndSource()
    {
        GoldsetTranslationId.Compose("ja", "ts-011").ShouldBe("i18n-ja--ts-011");
    }

    [TestCase("ja", "ts-011")]
    [TestCase("zh-CN", "ts-011")]
    [TestCase("zh-TW", "w05-coverage-add-ai-memory")]
    [TestCase("en", "ts-201")]
    public void TryParse_RoundTripsHyphenatedLocalesAndSources(string locale, string sourceId)
    {
        GoldsetTranslationId.TryParse(GoldsetTranslationId.Compose(locale, sourceId), out var parsedLocale, out var parsedSource)
            .ShouldBeTrue();

        parsedLocale.ShouldBe(locale);
        parsedSource.ShouldBe(sourceId);
    }

    [TestCase("ts-011")]
    [TestCase("para-ts-011-1")]
    [TestCase("i18n-ja-ts-011")]
    [TestCase("i18n-ja--")]
    [TestCase("i18n---ts-011")]
    [TestCase("I18N-ja--ts-011")]
    [TestCase("")]
    public void TryParse_RejectsIdsThatAreNotWellFormedTranslations(string itemId)
    {
        GoldsetTranslationId.TryParse(itemId, out _, out _).ShouldBeFalse();
    }

    [Test]
    public void IsTranslationId_OnlyLooksAtThePrefix()
    {
        GoldsetTranslationId.IsTranslationId("i18n-ja-ts-011").ShouldBeTrue();
        GoldsetTranslationId.IsTranslationId("ts-011").ShouldBeFalse();
    }
}
