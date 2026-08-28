// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the single hash source of the learning loop. The cases that matter are the ones the old
/// code got wrong: the gap detector normalised its hash and the trajectory capture did not, so the very
/// same utterance produced two different keys and no cluster ever met its own trajectory.
/// </summary>
namespace Klacks.UnitTest.Domain.Services.Assistant;

using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class MessageNormalizerTests
{
    [Test]
    public void Normalize_CollapsesWhitespaceTrimsAndLowercases()
    {
        MessageNormalizer.Normalize("  Zeige   MIR\tdie\nListe  ").ShouldBe("zeige mir die liste");
    }

    [Test]
    public void Normalize_BlankMessage_ReturnsEmpty()
    {
        MessageNormalizer.Normalize("   ").ShouldBe(string.Empty);
        MessageNormalizer.Normalize(null).ShouldBe(string.Empty);
    }

    [TestCase("Zeige mir die Liste", "  zeige   mir die liste ")]
    [TestCase("Wie viele Mitarbeiter?", "WIE VIELE MITARBEITER?")]
    public void Hash_IgnoresCasingAndWhitespace(string first, string second)
    {
        MessageNormalizer.Hash(first).ShouldBe(MessageNormalizer.Hash(second));
    }

    [Test]
    public void Hash_DifferentMessages_DifferentKeys()
    {
        MessageNormalizer.Hash("zeige mir die liste")
            .ShouldNotBe(MessageNormalizer.Hash("zeige mir die gruppe"));
    }

    [Test]
    public void Hash_IsSixteenLowercaseHexCharacters()
    {
        var hash = MessageNormalizer.Hash("Zeige mir die Liste");

        hash.Length.ShouldBe(MessageNormalizer.HashLength);
        hash.ShouldBe(hash.ToLowerInvariant());
        hash.ShouldAllBe(character => Uri.IsHexDigit(character));
    }

    [Test]
    public void Hash_BlankMessage_IsStableZeroKey()
    {
        MessageNormalizer.Hash(null).ShouldBe(new string('0', MessageNormalizer.HashLength));
    }

    // The composed and the decomposed spelling of the same accented word are the same utterance to a
    // human, and must therefore be the same cluster.
    [Test]
    public void Hash_UnicodeCompositionVariants_ProduceTheSameKey()
    {
        MessageNormalizer.Hash("Verfügbarkeit prüfen")
            .ShouldBe(MessageNormalizer.Hash("Verfügbarkeit prüfen"));
    }

    [Test]
    public void Excerpt_ShortMessage_IsReturnedWhole()
    {
        MessageNormalizer.Excerpt("  Kurze Frage  ", 120).ShouldBe("Kurze Frage");
    }

    [Test]
    public void Excerpt_LongMessage_CutsAtTheFirstSentenceEnd()
    {
        var message = "Erste Frage. " + new string('x', 200);

        MessageNormalizer.Excerpt(message, 120).ShouldBe("Erste Frage");
    }

    [Test]
    public void Excerpt_NeverExceedsTheLimit()
    {
        MessageNormalizer.Excerpt(new string('x', 500), 120).Length.ShouldBe(120);
    }

    [TestCase("", 0)]
    [TestCase("eins", 1)]
    [TestCase("zeige mir die liste", 4)]
    [TestCase("2 Schichten planen", 3)]
    public void CountWords_CountsLetterAndDigitRuns(string message, int expected)
    {
        MessageNormalizer.CountWords(message).ShouldBe(expected);
    }
}
