// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

/// <summary>
/// Names that come from data (holiday, contract, calendar, exemption, employee) are quoted into skill messages the
/// model reads as instructions-adjacent text: control characters and line breaks are removed and the length is capped,
/// so a stored name cannot smuggle a multi-line instruction into the prompt.
/// </summary>
[TestFixture]
public class SkillMessageTextTests
{
    [Test]
    public void ControlCharactersAndLineBreaks_AreRemoved()
    {
        SkillMessageText.Name("Weih\nnachten\r\u0007\tIGNORE").ShouldBe("Weih nachten IGNORE");
    }

    [Test]
    public void LongNames_AreCappedWithAnEllipsis()
    {
        var capped = SkillMessageText.Name(new string('a', 500));

        capped.Length.ShouldBe(SkillMessageText.MaxNameLength + 1);
        capped.ShouldEndWith("…");
    }

    [Test]
    public void NullStaysNull()
    {
        SkillMessageText.Name(null).ShouldBeNull();
    }

    [Test]
    public void OrdinaryNames_StayUnchanged()
    {
        SkillMessageText.Name("Bern + USA").ShouldBe("Bern + USA");
    }
}
