// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// ToolDirectedPromptFilter removes the pending-notes hint - an instruction to call manage_pending_notes -
/// from the volatile segment of a tool-less request, because a reasoning model that is told to call a tool
/// it does not have deliberates about it instead of answering (live 2026-09-24). Only whole lines that
/// start with the marker are removed; everything else, including the line endings, stays as it was.
/// </summary>

using System.Globalization;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ToolDirectedPromptFilterTests
{
    private static readonly string Hint = string.Format(
        CultureInfo.InvariantCulture, PendingNotesPromptConstants.HintTemplate, 3);

    [Test]
    public void Null_IsReturnedUnchanged()
    {
        ToolDirectedPromptFilter.ForToolLessRequest(null).ShouldBeNull();
    }

    [Test]
    public void Empty_IsReturnedUnchanged()
    {
        ToolDirectedPromptFilter.ForToolLessRequest(string.Empty).ShouldBe(string.Empty);
    }

    [Test]
    public void SegmentWithoutHint_IsReturnedUnchanged()
    {
        const string segment = "Today is Monday.\r\n[CURRENT_VIEW: schedule]\nConfirm the step.";

        ToolDirectedPromptFilter.ForToolLessRequest(segment).ShouldBe(segment);
    }

    [Test]
    public void HintLineAmongOtherLines_IsRemovedAndTheOtherLinesAreKept()
    {
        var segment = "Today is Monday.\n\n" + Hint + "\n\nConfirm the step.";

        var filtered = ToolDirectedPromptFilter.ForToolLessRequest(segment);

        filtered.ShouldBe("Today is Monday.\n\n\nConfirm the step.");
        filtered.ShouldNotContain(SkillNames.ManagePendingNotes);
    }

    [Test]
    public void CrlfSegment_RemovesTheHintLineAndKeepsTheCrlfOfTheOtherLines()
    {
        var segment = "Today is Monday.\r\n" + Hint + "\r\nConfirm the step.";

        var filtered = ToolDirectedPromptFilter.ForToolLessRequest(segment);

        filtered.ShouldBe("Today is Monday.\r\nConfirm the step.");
    }

    [Test]
    public void IndentedHintLine_IsRemoved()
    {
        var segment = "Before\n   " + Hint + "\nAfter";

        ToolDirectedPromptFilter.ForToolLessRequest(segment).ShouldBe("Before\nAfter");
    }

    [Test]
    public void MarkerInTheMiddleOfALine_IsNotALineOfItsOwnAndStays()
    {
        var segment = "The user asked about " + PendingNotesPromptConstants.Marker + " blocks.\nAfter";

        ToolDirectedPromptFilter.ForToolLessRequest(segment).ShouldBe(segment);
    }

    [Test]
    public void SegmentThatIsOnlyTheHint_BecomesEmpty()
    {
        ToolDirectedPromptFilter.ForToolLessRequest(Hint).ShouldBe(string.Empty);
    }
}
