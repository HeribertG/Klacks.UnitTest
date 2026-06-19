// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GroupPlaceClassificationParser: well-formed JSON, the live-observed unterminated JSON
/// (missing closing brace — the bug that mis-classified real cities as not-a-place), markdown-fenced and
/// prose-wrapped JSON, confidence clamping, and unrecoverable input falling back to NotAPlace.
/// </summary>

using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Application.Interfaces.Grouping;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class GroupPlaceClassificationParserTests
{
    [Test]
    public void Parse_WellFormedJson_ReturnsClassification()
    {
        var result = GroupPlaceClassificationParser.Parse(
            "{\"isPlace\": true, \"canonicalName\": \"Thun\", \"region\": \"Bern\", \"confidence\": 0.95}");

        result.IsPlace.ShouldBeTrue();
        result.CanonicalName.ShouldBe("Thun");
        result.Confidence.ShouldBe(0.95);
    }

    [Test]
    public void Parse_UnterminatedJson_MissingClosingBrace_StillParses()
    {
        // Exactly the gemini-3.5-flash output observed live: valid JSON but no closing brace.
        var result = GroupPlaceClassificationParser.Parse(
            "{\"isPlace\": true, \"canonicalName\": \"Thun\", \"region\": \"Bern\", \"confidence\": 1");

        result.IsPlace.ShouldBeTrue();
        result.CanonicalName.ShouldBe("Thun");
        result.Confidence.ShouldBe(1.0);
    }

    [Test]
    public void Parse_MarkdownFencedJson_Parses()
    {
        var result = GroupPlaceClassificationParser.Parse(
            "```json\n{\"isPlace\": false, \"canonicalName\": null, \"region\": null, \"confidence\": 0.9}\n```");

        result.IsPlace.ShouldBeFalse();
    }

    [Test]
    public void Parse_ProseWrappedJson_Parses()
    {
        var result = GroupPlaceClassificationParser.Parse(
            "Here is the classification: {\"isPlace\": true, \"canonicalName\": \"Biel\", \"confidence\": 0.8} done.");

        result.IsPlace.ShouldBeTrue();
        result.CanonicalName.ShouldBe("Biel");
    }

    [Test]
    public void Parse_ConfidenceOutOfRange_IsClamped()
    {
        var result = GroupPlaceClassificationParser.Parse(
            "{\"isPlace\": true, \"confidence\": 5.0}");

        result.Confidence.ShouldBe(1.0);
    }

    [TestCase("")]
    [TestCase("   ")]
    [TestCase("no json here at all")]
    public void Parse_Unrecoverable_ReturnsNotAPlace(string content)
    {
        var result = GroupPlaceClassificationParser.Parse(content);

        result.IsPlace.ShouldBeFalse();
        result.Confidence.ShouldBe(0.0);
    }
}
