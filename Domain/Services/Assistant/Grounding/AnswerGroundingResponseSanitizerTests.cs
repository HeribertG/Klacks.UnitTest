// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Sanitizer truth table: the server-authored no-action notice disappears, everything from the
/// recipe-failure prefix on is cut, and [SUGGESTIONS]/[REPLIES] UI blocks are stripped while the
/// model's own prose stays intact.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Grounding;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant.Grounding;

[TestFixture]
public class AnswerGroundingResponseSanitizerTests
{
    [Test]
    public void NoActionNotice_IsRemoved()
    {
        var text = "Ich habe nichts geändert." + MutationGuardConstants.NoActionStreamNotice;

        AnswerGroundingResponseSanitizer.Sanitize(text).ShouldBe("Ich habe nichts geändert.");
    }

    [Test]
    public void EverythingAfterRecipeFailurePrefix_IsCut()
    {
        var text = "Schritt eins ist erledigt." + MutationGuardConstants.RecipeStepFailedNoticePrefix + "Server-Anhang mit 999 Details.";

        var sanitized = AnswerGroundingResponseSanitizer.Sanitize(text);

        sanitized.ShouldBe("Schritt eins ist erledigt.");
        sanitized.ShouldNotContain("999");
    }

    [Test]
    public void SuggestionsAndRepliesBlocks_AreStripped()
    {
        var text = """Hier die Antwort. [SUGGESTIONS: "Öffne Planung" | "Zeig Ferien"] [REPLIES:single "Ja" "Nein"] Ende.""";

        var sanitized = AnswerGroundingResponseSanitizer.Sanitize(text);

        sanitized.ShouldNotContain("SUGGESTIONS");
        sanitized.ShouldNotContain("REPLIES");
        sanitized.ShouldContain("Hier die Antwort.");
        sanitized.ShouldContain("Ende.");
    }

    [Test]
    public void EmptyInput_YieldsEmptyOutput()
    {
        AnswerGroundingResponseSanitizer.Sanitize(string.Empty).ShouldBe(string.Empty);
    }
}
