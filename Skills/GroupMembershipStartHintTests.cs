// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for GroupMembershipStartHint: the deterministic sentences state the start date and the blue-and-locked
/// consequence in the preview and the result, name the persons who entered earlier, ask the entry-date question only in a
/// preview, say nothing without persons, and the single-person variant only speaks when it matters. The sentences carry no
/// enum or code names, since the model relays them in the user's language.
/// </summary>

using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GroupMembershipStartHintTests
{
    private static GroupMembershipStartInfo Fixed(int enteredEarlier, bool given = true) =>
        new(GroupMembershipStartMode.Fixed, given, "2026-10-20", "2026-10-20", "2026-10-20", 5, enteredEarlier, 0, enteredEarlier > 0 ? "2024-02-01" : null);

    private static GroupMembershipStartInfo Entry(int withoutEntry = 0) =>
        new(GroupMembershipStartMode.EntryDate, true, null, "2019-03-01", "2025-08-15", 5, 0, withoutEntry, null);

    [Test]
    public void NullOrNoPersons_SaysNothing()
    {
        GroupMembershipStartHint.For(null, applied: false).ShouldBeEmpty();
        GroupMembershipStartHint.For(Fixed(0) with { PersonCount = 0 }, applied: true).ShouldBeEmpty();
    }

    [Test]
    public void FixedPreview_WithEarlierEntries_StatesCountEarliestEntryAndAsks()
    {
        var text = GroupMembershipStartHint.For(Fixed(3), applied: false);

        text.ShouldContain("will start on 2026-10-20");
        text.ShouldContain("blue and locked");
        text.ShouldContain("3 of 5 person(s)");
        text.ShouldContain("earliest entry 2024-02-01");
        text.ShouldContain("validFrom='entry'");
        text.ShouldContain("do not apply before the user has answered");
        text.ShouldNotContain("Tell the user this");
    }

    [Test]
    public void FixedApplied_WithEarlierEntries_StatesCountAndTellsTheUser_WithoutAsking()
    {
        var text = GroupMembershipStartHint.For(Fixed(3), applied: true);

        text.ShouldContain("start on 2026-10-20");
        text.ShouldContain("3 of 5 person(s)");
        text.ShouldContain("Tell the user this");
        text.ShouldNotContain("validFrom='entry'");
    }

    [Test]
    public void FixedPreview_WithoutEarlierEntries_StatesTheDateAndDoesNotAsk()
    {
        var text = GroupMembershipStartHint.For(Fixed(0), applied: false);

        text.ShouldContain("blue and locked");
        text.ShouldNotContain("entered the company earlier");
        text.ShouldNotContain("validFrom='entry'");
    }

    [Test]
    public void DefaultedDate_IsNamedAsTheDefault()
    {
        GroupMembershipStartHint.For(Fixed(0, given: false), applied: false).ShouldContain("default start date 2026-10-20");
        GroupMembershipStartHint.For(Fixed(0, given: false), applied: true).ShouldContain("default date 2026-10-20");
    }

    [Test]
    public void EntryMode_StatesTheRangeAndWhoStartsOnTheCompanyDay()
    {
        var text = GroupMembershipStartHint.For(Entry(withoutEntry: 2), applied: true);

        text.ShouldContain("each person's own employment entry date");
        text.ShouldContain("earliest 2019-03-01, latest 2025-08-15");
        text.ShouldContain("2 person(s) have no entry date");
        text.ShouldContain("Tell the user this");
    }

    [Test]
    public void Single_SaysNothing_WhenNobodyEnteredEarlier()
    {
        GroupMembershipStartHint.ForSingle(Fixed(0) with { PersonCount = 1 }).ShouldBeEmpty();
    }

    [Test]
    public void Single_Speaks_WhenThePersonEnteredEarlier_OrEntryModeWasUsed()
    {
        GroupMembershipStartHint.ForSingle(Fixed(1) with { PersonCount = 1 }).ShouldContain("blue and locked");
        GroupMembershipStartHint.ForSingle(Entry() with { PersonCount = 1 }).ShouldContain("each person's own employment entry date");
    }

    [Test]
    public void ExistingMembersStartingLater_AreReportedAsLeftUnchanged_EvenWithoutNewPersons()
    {
        var info = Entry() with
        {
            PersonCount = 0,
            ExistingMembersStartingAfterEntryCount = 4,
            ExistingMembersEarliestEntryDate = "2024-02-01"
        };

        var preview = GroupMembershipStartHint.For(info, applied: false);
        var applied = GroupMembershipStartHint.For(info, applied: true);

        preview.ShouldNotBeEmpty();
        preview.ShouldContain("4 existing member(s)");
        preview.ShouldContain("earliest entry 2024-02-01");
        preview.ShouldContain("does not change");
        preview.ShouldContain("blue and locked");
        applied.ShouldContain("Tell the user this");
    }

    [Test]
    public void ExistingMembersSentence_IsAddedToTheEntryModeText_WhenNewPersonsExist()
    {
        var info = Entry() with { ExistingMembersStartingAfterEntryCount = 2, ExistingMembersEarliestEntryDate = "2023-05-01" };

        var text = GroupMembershipStartHint.For(info, applied: true);

        text.ShouldContain("each person's own employment entry date");
        text.ShouldContain("2 existing member(s)");
    }

    [Test]
    public void NoPersonsAndNoExistingMembers_StaysEmpty()
    {
        GroupMembershipStartHint.For(Entry() with { PersonCount = 0 }, applied: true).ShouldBeEmpty();
    }
}
