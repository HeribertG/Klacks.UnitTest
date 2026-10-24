// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for SearchStringTokenizer — separator splitting, quoted phrases (straight and typographic
/// quotes, unclosed and empty quotes), apostrophes in names and the '+' alternatives with quotes stripped.
/// </summary>

using Klacks.Api.Domain.Services.Common;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Common;

[TestFixture]
public class SearchStringTokenizerTests
{
    [Test]
    public void Tokenize_WithoutQuotes_SplitsOnAllSeparators()
    {
        SearchStringTokenizer.Tokenize("Müller, Hans-Peter")
            .ShouldBe(new[] { "müller", "hans", "peter" });
    }

    [Test]
    public void Tokenize_WithQuotedPhrase_KeepsPhraseAsOneToken()
    {
        SearchStringTokenizer.Tokenize("Karl \"von Oberhausen\"")
            .ShouldBe(new[] { "karl", "von oberhausen" });
    }

    [TestCase("„von Oberhausen“")]
    [TestCase("“von Oberhausen”")]
    [TestCase("«von Oberhausen»")]
    public void Tokenize_WithTypographicQuotes_KeepsPhraseAsOneToken(string searchString)
    {
        SearchStringTokenizer.Tokenize(searchString).ShouldBe(new[] { "von oberhausen" });
    }

    [Test]
    public void Tokenize_WithUnclosedQuote_TreatsRestAsPhrase()
    {
        SearchStringTokenizer.Tokenize("Karl \"von Ober")
            .ShouldBe(new[] { "karl", "von ober" });
    }

    [Test]
    public void Tokenize_WithEmptyQuotes_IgnoresThem()
    {
        SearchStringTokenizer.Tokenize("\"\" Müller \"  \"")
            .ShouldBe(new[] { "müller" });
    }

    [Test]
    public void Tokenize_WithRepeatedSpacesInPhrase_CollapsesThem()
    {
        SearchStringTokenizer.Tokenize("\"  de   Caprio \"")
            .ShouldBe(new[] { "de caprio" });
    }

    [Test]
    public void Tokenize_WithPhraseContainingSeparators_KeepsThem()
    {
        SearchStringTokenizer.Tokenize("\"Müller-Lüdenscheidt\"")
            .ShouldBe(new[] { "müller-lüdenscheidt" });
    }

    [Test]
    public void Tokenize_WithApostrophe_DoesNotTreatItAsQuote()
    {
        SearchStringTokenizer.Tokenize("O'Brien Sean")
            .ShouldBe(new[] { "o'brien", "sean" });
    }

    [Test]
    public void SplitAlternatives_StripsQuotesFromEachAlternative()
    {
        SearchStringTokenizer.SplitAlternatives("\"von Oberhausen\"+Meier")
            .ShouldBe(new[] { "von oberhausen", "meier" });
    }

    [Test]
    public void HasAlternatives_WithPlusOutsideQuotes_IsTrue()
    {
        SearchStringTokenizer.HasAlternatives("Müller+Meier").ShouldBeTrue();
    }

    [Test]
    public void HasAlternatives_WithPlusOnlyInsideQuotes_IsFalse()
    {
        SearchStringTokenizer.HasAlternatives("\"Müller+Partner\" AG").ShouldBeFalse();
    }

    [Test]
    public void Tokenize_WithPlusInsideQuotes_KeepsItInThePhrase()
    {
        SearchStringTokenizer.Tokenize("\"Müller+Partner\"")
            .ShouldBe(new[] { "müller+partner" });
    }

    [Test]
    public void SplitAlternatives_WithPlusInsideQuotes_SplitsOnlyOutside()
    {
        SearchStringTokenizer.SplitAlternatives("\"Müller+Partner\"+Meier")
            .ShouldBe(new[] { "müller+partner", "meier" });
    }

    [Test]
    public void StripQuotes_RemovesStraightAndTypographicQuotes()
    {
        SearchStringTokenizer.StripQuotes("„von“ \"Oberhausen\" «x»")
            .ShouldBe("von Oberhausen x");
    }
}
