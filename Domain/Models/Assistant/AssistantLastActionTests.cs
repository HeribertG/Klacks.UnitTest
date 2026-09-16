// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// CanAnchorCorrection is gate G0 of the correction path: a record may anchor a correction only while
/// it is younger than GracefulCorrectionDefaults.CorrectionWindowMinutes, even though the row itself
/// survives for the longer GracefulCorrectionDefaults.LastActionTtlMinutes (its clarification pins are
/// read after the anchor itself has gone stale).
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Models.Assistant;

[TestFixture]
public class AssistantLastActionTests
{
    private static AssistantLastAction Action(DateTime createTimeUtc) => new()
    {
        UserId = Guid.NewGuid(),
        ConversationId = "conv-1",
        UserMessage = "add a shift",
        Calls = [new AssistantLastActionCall { SkillName = "add_shift_to_group", Success = true }],
        CreateTimeUtc = createTimeUtc
    };

    [Test]
    public void CanAnchorCorrection_IsTrue_InsideTheCorrectionWindow()
    {
        var now = DateTime.UtcNow;
        var action = Action(now.AddMinutes(-(GracefulCorrectionDefaults.CorrectionWindowMinutes - 1)));

        action.CanAnchorCorrection(now).ShouldBeTrue();
    }

    [Test]
    public void CanAnchorCorrection_IsFalse_OutsideTheCorrectionWindow()
    {
        var now = DateTime.UtcNow;
        var action = Action(now.AddMinutes(-(GracefulCorrectionDefaults.CorrectionWindowMinutes + 1)));

        action.CanAnchorCorrection(now).ShouldBeFalse();
    }
}
