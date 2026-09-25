// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for DoubleBraceTemplate: every {{name}} gets its value, an inserted value is never expanded a
/// second time (the old sequential Replace expanded a "{{employee}}" inside an earlier value), a placeholder
/// without a value stays standing, single-brace text is left alone, and a pathological template with
/// thousands of half-open braces is rendered in well under a second.
/// </summary>

using System.Diagnostics;
using Klacks.Api.Domain.Services.Common;

namespace Klacks.UnitTest.Domain.Services.Common;

[TestFixture]
public class DoubleBraceTemplateTests
{
    private const int PathologicalRepeats = 20000;
    private static readonly TimeSpan PathologicalBudget = TimeSpan.FromSeconds(1);

    [Test]
    public void Render_FillsEveryPlaceholder()
    {
        var text = DoubleBraceTemplate.Render(
            "{{a}} and {{b}} and {{a}}",
            new Dictionary<string, string> { ["a"] = "1", ["b"] = "2" });

        text.ShouldBe("1 and 2 and 1");
    }

    [Test]
    public void Render_NeverExpandsAnInsertedValueASecondTime()
    {
        var text = DoubleBraceTemplate.Render(
            "{{responder}} took {{date}}",
            new Dictionary<string, string> { ["responder"] = "Eve {{date}}", ["date"] = "16.08.2026" });

        text.ShouldBe("Eve {{date}} took 16.08.2026");
    }

    [Test]
    public void Render_LeavesAPlaceholderWithoutAValueStanding()
    {
        var text = DoubleBraceTemplate.Render(
            "{{date}} in {{days}}", new Dictionary<string, string> { ["date"] = "16.08.2026" });

        text.ShouldBe("16.08.2026 in {{days}}");
    }

    [Test]
    public void Render_WithoutValues_ReturnsTheTemplateUnchanged()
    {
        DoubleBraceTemplate.Render("{{date}}", null).ShouldBe("{{date}}");
        DoubleBraceTemplate.Render("{{date}}", new Dictionary<string, string>()).ShouldBe("{{date}}");
    }

    [Test]
    public void Render_LeavesSingleBracesAndNonAsciiNamesAlone()
    {
        var text = DoubleBraceTemplate.Render(
            "{date} {{da te}} {{日付}} {{{date}}}",
            new Dictionary<string, string> { ["date"] = "X" });

        text.ShouldBe("{date} {{da te}} {{日付}} {X}");
    }

    [Test]
    public void PlaceholdersOf_ReturnsTheDistinctNames()
    {
        DoubleBraceTemplate.PlaceholdersOf("{{a}} {{b1}} {{a}} {c}")
            .ShouldBe(new HashSet<string> { "a", "b1" }, ignoreOrder: true);
    }

    [Test]
    public void Render_APathologicalTemplate_FinishesQuickly()
    {
        var template = string.Concat(Enumerable.Repeat("{{{{a", PathologicalRepeats)) + "}}";
        var stopwatch = Stopwatch.StartNew();

        DoubleBraceTemplate.Render(template, new Dictionary<string, string> { ["a"] = "x" });
        DoubleBraceTemplate.PlaceholdersOf(template);

        stopwatch.Elapsed.ShouldBeLessThan(PathologicalBudget);
    }
}
