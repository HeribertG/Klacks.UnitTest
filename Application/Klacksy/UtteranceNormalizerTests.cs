// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Klacksy;

using FluentAssertions;
using Klacks.Api.Application.Klacksy;
using NUnit.Framework;

[TestFixture]
public class UtteranceNormalizerTests
{
    private IUtteranceNormalizer _sut = null!;

    [SetUp]
    public void SetUp() => _sut = new UtteranceNormalizer();

    [TestCase("Klacksy, zeig mir LLM Provider", "de", "zeig mir llm provider", true)]
    [TestCase("Hey Klacksy wo ist LLM", "en", "wo ist llm", true)]
    [TestCase("hallo klacksy settings bitte", "de", "settings", true)]
    [TestCase("Salut Klacksy", "fr", "", true)]
    [TestCase("klaxy llm provider", "de", "llm provider", true)]
    [TestCase("LLM Provider", "de", "llm provider", false)]
    public void Normalize_strips_wake_words_and_fillers(string raw, string locale, string expected, bool stripped)
    {
        var result = _sut.Normalize(raw, locale);
        result.Normalized.Should().Be(expected);
        result.WakeWordStripped.Should().Be(stripped);
        result.Original.Should().Be(raw);
    }

    [Test]
    public void Normalize_flags_empty_after_stripping()
    {
        var result = _sut.Normalize("Klacksy", "de");
        result.IsEmptyAfterNormalization.Should().BeTrue();
    }

    [Test]
    public void Normalize_preserves_non_wake_word_input()
    {
        var result = _sut.Normalize("wo ist die Einstellung", "de");
        result.Normalized.Should().Be("wo ist die einstellung");
        result.WakeWordStripped.Should().BeFalse();
    }
}
