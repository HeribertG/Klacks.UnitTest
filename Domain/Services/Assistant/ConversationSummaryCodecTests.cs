// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ConversationSummaryCodec: strict-but-tolerant JSON parsing (fence stripping,
/// non-empty-section gate), guaranteed length-bounded serialization, and rendering of both the
/// structured form and legacy free-text summaries into a compact system block.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ConversationSummaryCodecTests
{
    private const string ValidJson =
        "{\"openTasks\":[\"Finish the roster\"],\"touchedEntities\":[{\"type\":\"Client\",\"name\":\"Anna\",\"id\":\"c-1\"}],\"decisions\":[\"Use the night shift\"],\"facts\":[\"User prefers mornings\"]}";

    [Test]
    public void TryParse_ValidJson_ReturnsStructured()
    {
        var ok = ConversationSummaryCodec.TryParse(ValidJson, out var summary);

        ok.ShouldBeTrue();
        summary.OpenTasks.ShouldContain("Finish the roster");
        summary.Decisions.ShouldContain("Use the night shift");
        summary.Facts.ShouldContain("User prefers mornings");
        summary.TouchedEntities.Count.ShouldBe(1);
        summary.TouchedEntities[0].Type.ShouldBe("Client");
        summary.TouchedEntities[0].Name.ShouldBe("Anna");
        summary.TouchedEntities[0].Id.ShouldBe("c-1");
    }

    [Test]
    public void TryParse_MarkdownFencedJson_StripsFencesAndParses()
    {
        var fenced = "Here you go:\n```json\n" + ValidJson + "\n```";

        var ok = ConversationSummaryCodec.TryParse(fenced, out var summary);

        ok.ShouldBeTrue();
        summary.OpenTasks.ShouldContain("Finish the roster");
    }

    [Test]
    public void TryParse_CaseInsensitiveKeys_Parses()
    {
        var ok = ConversationSummaryCodec.TryParse(
            "{\"OpenTasks\":[\"A\"],\"Facts\":[\"B\"]}", out var summary);

        ok.ShouldBeTrue();
        summary.OpenTasks.ShouldContain("A");
        summary.Facts.ShouldContain("B");
    }

    [Test]
    public void TryParse_EmptyObject_ReturnsFalse()
    {
        ConversationSummaryCodec.TryParse("{}", out _).ShouldBeFalse();
    }

    [Test]
    public void TryParse_AllSectionsEmpty_ReturnsFalse()
    {
        var allEmpty = "{\"openTasks\":[],\"touchedEntities\":[],\"decisions\":[],\"facts\":[]}";

        ConversationSummaryCodec.TryParse(allEmpty, out _).ShouldBeFalse();
    }

    [Test]
    public void TryParse_FreeText_ReturnsFalse()
    {
        ConversationSummaryCodec.TryParse("The user asked about shift planning.", out _)
            .ShouldBeFalse();
    }

    [Test]
    public void TryParse_NullOrWhitespace_ReturnsFalse()
    {
        ConversationSummaryCodec.TryParse(null, out _).ShouldBeFalse();
        ConversationSummaryCodec.TryParse("   ", out _).ShouldBeFalse();
    }

    [Test]
    public void TryParse_IgnoresBlankEntriesAndEmptyEntities()
    {
        var json =
            "{\"openTasks\":[\"\",\"  \",\"Real task\"],\"touchedEntities\":[{\"type\":\"\",\"name\":\"\"}]}";

        var ok = ConversationSummaryCodec.TryParse(json, out var summary);

        ok.ShouldBeTrue();
        summary.OpenTasks.Count.ShouldBe(1);
        summary.OpenTasks.ShouldContain("Real task");
        summary.TouchedEntities.ShouldBeEmpty();
    }

    [Test]
    public void Serialize_RoundTrips()
    {
        ConversationSummaryCodec.TryParse(ValidJson, out var summary);

        var json = ConversationSummaryCodec.Serialize(summary);
        var ok = ConversationSummaryCodec.TryParse(json, out var reparsed);

        ok.ShouldBeTrue();
        reparsed.OpenTasks.ShouldContain("Finish the roster");
    }

    [Test]
    public void Fit_UnderBound_KeepsAllContent()
    {
        ConversationSummaryCodec.TryParse(ValidJson, out var summary);

        var json = ConversationSummaryCodec.Fit(summary, 2000);

        ConversationSummaryCodec.TryParse(json, out var reparsed).ShouldBeTrue();
        reparsed.Facts.ShouldContain("User prefers mornings");
        reparsed.OpenTasks.ShouldContain("Finish the roster");
    }

    [Test]
    public void Fit_OverBound_TrimsToBudgetAndStaysValidJson()
    {
        var summary = new StructuredConversationSummary();
        for (var i = 0; i < 12; i++)
        {
            summary.OpenTasks.Add(new string('t', 80) + i);
            summary.Facts.Add(new string('f', 80) + i);
            summary.Decisions.Add(new string('d', 80) + i);
        }

        const int bound = 300;
        var json = ConversationSummaryCodec.Fit(summary, bound);

        json.Length.ShouldBeLessThanOrEqualTo(bound);
        ConversationSummaryCodec.TryParse(json, out _).ShouldBeTrue();
    }

    [Test]
    public void Fit_DropsFactsBeforeOpenTasks()
    {
        var summary = new StructuredConversationSummary();
        summary.OpenTasks.Add("Keep me: pending task");
        for (var i = 0; i < 10; i++)
        {
            summary.Facts.Add(new string('f', 120) + i);
        }

        var json = ConversationSummaryCodec.Fit(summary, 200);

        ConversationSummaryCodec.TryParse(json, out var reparsed).ShouldBeTrue();
        reparsed.OpenTasks.ShouldContain("Keep me: pending task");
    }

    [Test]
    public void RenderInner_Null_ReturnsNull()
    {
        ConversationSummaryCodec.RenderInner(null).ShouldBeNull();
        ConversationSummaryCodec.RenderInner("   ").ShouldBeNull();
    }

    [Test]
    public void RenderInner_FreeText_ReturnsRawVerbatim()
    {
        const string raw = "The user is a nurse in Bern and works nights.";

        ConversationSummaryCodec.RenderInner(raw).ShouldBe(raw);
    }

    [Test]
    public void RenderInner_Structured_RendersSectionsNotRawJson()
    {
        var rendered = ConversationSummaryCodec.RenderInner(ValidJson);

        rendered.ShouldNotBeNull();
        rendered!.ShouldContain("Open tasks:");
        rendered.ShouldContain("- Finish the roster");
        rendered.ShouldContain("Entities:");
        rendered.ShouldContain("- Client Anna (c-1)");
        rendered.ShouldContain("Decisions:");
        rendered.ShouldContain("Facts:");
        rendered.ShouldNotContain("{");
        rendered.ShouldNotContain("\"openTasks\"");
    }

    [Test]
    public void RenderInner_Structured_OmitsEmptySections()
    {
        var rendered = ConversationSummaryCodec.RenderInner("{\"facts\":[\"Only a fact\"]}");

        rendered.ShouldNotBeNull();
        rendered!.ShouldContain("Facts:");
        rendered.ShouldContain("- Only a fact");
        rendered.ShouldNotContain("Open tasks:");
        rendered.ShouldNotContain("Entities:");
        rendered.ShouldNotContain("Decisions:");
    }

    [Test]
    public void RenderInner_EntityWithoutId_OmitsParentheses()
    {
        var rendered = ConversationSummaryCodec.RenderInner(
            "{\"touchedEntities\":[{\"type\":\"Shift\",\"name\":\"Early\"}]}");

        rendered.ShouldNotBeNull();
        rendered!.ShouldContain("- Shift Early");
        rendered.ShouldNotContain("(");
    }
}
