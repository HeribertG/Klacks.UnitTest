// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class TtsTextChunkerTests
{
    [Test]
    public void Split_ShortText_ReturnsSingleChunk()
    {
        var chunks = TtsTextChunker.Split("Hallo Welt.", 100);

        chunks.Count.ShouldBe(1);
        chunks[0].ShouldBe("Hallo Welt.");
    }

    [Test]
    public void Split_LongText_SplitsAtSentenceBoundaries()
    {
        var text = string.Concat(Enumerable.Repeat("Dies ist ein Satz. ", 50));

        var chunks = TtsTextChunker.Split(text, 100);

        chunks.Count.ShouldBeGreaterThan(1);
        chunks.ShouldAllBe(c => c.Length <= 100);
        chunks.ShouldAllBe(c => c.TrimEnd().EndsWith('.'));
    }

    [Test]
    public void Split_ConcatenatedChunks_ReproduceOriginalText()
    {
        var text = string.Concat(Enumerable.Repeat("Erster Satz! Zweiter Satz? Dritter Satz.\n", 40));

        var chunks = TtsTextChunker.Split(text, 120);

        string.Concat(chunks).ShouldBe(text);
    }

    [Test]
    public void Split_SentenceLongerThanChunk_FallsBackToWordBoundary()
    {
        var text = string.Join(' ', Enumerable.Repeat("wort", 100));

        var chunks = TtsTextChunker.Split(text, 50);

        chunks.ShouldAllBe(c => c.Length <= 50);
        string.Concat(chunks).ShouldBe(text);
    }

    [Test]
    public void Split_TextWithoutAnyBoundary_HardSplits()
    {
        var text = new string('a', 130);

        var chunks = TtsTextChunker.Split(text, 50);

        chunks.Count.ShouldBe(3);
        string.Concat(chunks).ShouldBe(text);
    }

    [Test]
    public void Split_InvalidMaxLength_Throws()
    {
        Should.Throw<ArgumentOutOfRangeException>(() => TtsTextChunker.Split("Text", 0));
    }
}
