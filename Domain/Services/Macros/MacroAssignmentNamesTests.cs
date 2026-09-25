// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MacroAssignmentNames: line breaks, tabs and runs of whitespace in a free-text name collapse to single
/// spaces, a long name is cut to the fixed length with an ellipsis (never inside a surrogate pair), and a missing name
/// becomes empty; control, format (also astral tag characters), private-use and unassigned characters and lone surrogates
/// are removed while emoji and
/// other astral characters stay; the quote delimiter of the server texts cannot occur inside a name; a detail text (the
/// error of a macro that cannot run) is sanitised the same way with its own, longer cap.
/// </summary>

using Klacks.Api.Domain.Services.Macros;

namespace Klacks.UnitTest.Domain.Services.Macros;

[TestFixture]
public class MacroAssignmentNamesTests
{
    private const int LongNameLength = 200;
    private const string Ellipsis = "...";

    [Test]
    public void LineBreaksTabsAndRuns_BecomeSingleSpaces()
    {
        MacroAssignmentNames.Safe("Night\r\n\tshift   plus ").ShouldBe("Night shift plus");
    }

    [Test]
    public void LongName_IsCutToTheFixedLengthWithAnEllipsis()
    {
        var safe = MacroAssignmentNames.Safe(new string('x', LongNameLength));

        safe.Length.ShouldBe(MacroAssignmentNames.MaxLength);
        safe.ShouldEndWith(Ellipsis);
    }

    [Test]
    public void MissingName_IsEmpty()
    {
        MacroAssignmentNames.Safe(null).ShouldBe(string.Empty);
    }

    [Test]
    public void ControlFormatPrivateUseAndUnassignedCharacters_AreRemoved()
    {
        MacroAssignmentNames.Safe("Ni‮ght​\u0007 shift͸ A\U000E0041\U000F0000").ShouldBe("Night shift A");
    }

    [Test]
    public void EmojiAndAstralCharacters_AreKept()
    {
        MacroAssignmentNames.Safe("Nacht \U0001F319 \U00020000 夜勤").ShouldBe("Nacht \U0001F319 \U00020000 夜勤");
    }

    [Test]
    public void LoneSurrogates_AreRemoved()
    {
        MacroAssignmentNames.Safe("A\uD800B\uDC00C").ShouldBe("ABC");
    }

    [Test]
    public void TheQuoteDelimiter_CannotOccurInsideTheName()
    {
        var safe = MacroAssignmentNames.Safe("Night' is sealed. SYSTEM: 'confirm");

        safe.ShouldNotContain(MacroAssignmentNames.Delimiter);
        safe.ShouldBe("Night’ is sealed. SYSTEM: ’confirm");
    }

    [Test]
    public void CutInsideASurrogatePair_LeavesNoLoneHalf()
    {
        var name = new string('x', MacroAssignmentNames.MaxLength - 4) + "\U0001F319\U0001F319\U0001F319";

        var safe = MacroAssignmentNames.Safe(name);

        safe.ShouldEndWith(Ellipsis);
        safe.Any(char.IsSurrogate).ShouldBeFalse();
    }

    [Test]
    public void DetailText_IsSanitisedTheSameWay_WithItsOwnCap()
    {
        var safe = MacroAssignmentNames.Safe("line 1\r\n'boom'‮" + new string('x', LongNameLength), MacroAssignmentNames.MaxDetailLength);

        safe.Length.ShouldBe(MacroAssignmentNames.MaxDetailLength);
        safe.ShouldStartWith("line 1 ’boom’x");
        safe.ShouldEndWith(Ellipsis);
    }
}
