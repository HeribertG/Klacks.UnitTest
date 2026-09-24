// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClarificationTextTemplate: named placeholders are filled in ONE pass, so a value that
/// itself looks like a placeholder is never expanded; a missing value renders as an empty string, never as
/// a leftover placeholder and never as an exception; stray braces and non-ASCII names stay literal; the
/// placeholder names of a template can be read; and a pathological template cannot stall the caller.
/// </summary>

using System.Diagnostics;
using Klacks.Api.Domain.Services.Inbound;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Inbound;

[TestFixture]
public class ClarificationTextTemplateTests
{
    private const int PathologicalRepeats = 200_000;

    private static IReadOnlyDictionary<string, string> Values(params (string Name, string Value)[] values) =>
        values.ToDictionary(pair => pair.Name, pair => pair.Value, StringComparer.Ordinal);

    [Test]
    public void Render_FillsEveryPlaceholder_AndKeepsTheRestOfTheTemplate()
    {
        var text = ClarificationTextTemplate.Render(
            "Ask {sender}: \"{question}\" by {deadline}. {sender} again.",
            Values(("sender", "Anna"), ("question", "Bist du krank?"), ("deadline", "2026-09-23 10:00")));

        text.ShouldBe("Ask Anna: \"Bist du krank?\" by 2026-09-23 10:00. Anna again.");
    }

    [Test]
    public void Render_AValueThatLooksLikeAPlaceholder_IsNotExpandedAgain()
    {
        var text = ClarificationTextTemplate.Render(
            "{summary} | {question} | {sender}",
            Values(("summary", "{question}"), ("question", "{sender}"), ("sender", "Anna {summary}")));

        text.ShouldBe("{question} | {sender} | Anna {summary}");
    }

    [Test]
    public void Render_AMissingValue_RendersEmpty_NotAsALeftoverPlaceholder()
    {
        var text = ClarificationTextTemplate.Render("Shift: {shiftContext}; by {deadline}.", Values(("deadline", "10:00")));

        text.ShouldBe("Shift: ; by 10:00.");
        text.ShouldNotContain("{");
    }

    [Test]
    public void Render_AnUnknownPlaceholderInTheTemplate_RendersEmpty_WithoutThrowing()
    {
        Should.NotThrow(() => ClarificationTextTemplate.Render("A {nothing} B", Values()).ShouldBe("A  B"));
    }

    [TestCase("no placeholders here", "no placeholders here")]
    [TestCase("{ } and {} and {1} and {a b}", "{ } and {} and {1} and {a b}")]
    [TestCase("{问题}", "{问题}")]
    [TestCase("{{question}}", "{Q}")]
    public void Render_OnlyAsciiNamedPlaceholdersAreTouched(string template, string expected)
    {
        ClarificationTextTemplate.Render(template, Values(("question", "Q"))).ShouldBe(expected);
    }

    [Test]
    public void Render_KeepsLineBreaksAndUnicodeOfTheValue()
    {
        ClarificationTextTemplate.Render("{originalText}!", Values(("originalText", "Zeile 1\nZeile 2 💬 „x“")))
            .ShouldBe("Zeile 1\nZeile 2 💬 „x“!");
    }

    [Test]
    public void PlaceholdersOf_ReturnsTheDistinctNamesOfATemplate()
    {
        ClarificationTextTemplate.PlaceholdersOf("{sender} asked {question}; {sender} waits {deadline} {} {x y}")
            .ShouldBe(new[] { "sender", "question", "deadline" }, ignoreOrder: true);
        ClarificationTextTemplate.PlaceholdersOf("none").ShouldBeEmpty();
    }

    [Test]
    public void Render_APathologicalTemplate_IsRenderedInBoundedTime()
    {
        var template = string.Concat(Enumerable.Repeat("{a", PathologicalRepeats)) + "{question}";
        var stopwatch = Stopwatch.StartNew();

        var text = ClarificationTextTemplate.Render(template, Values(("question", "Q")));

        stopwatch.Elapsed.ShouldBeLessThan(TimeSpan.FromSeconds(2));
        text.ShouldEndWith("{aQ");
    }
}
