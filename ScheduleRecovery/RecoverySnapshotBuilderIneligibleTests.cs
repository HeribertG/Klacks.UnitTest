// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.ScheduleOptimizer.Models;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.ScheduleRecovery.Model;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Unit tests for the ineligibility merge of <see cref="RecoverySnapshotBuilder"/>: availability stays hard on keyword
/// days (owner decision 2026-11-05, as in the wizards) and qualification is sharp on every day, so both sources are
/// kept in full; plus the keyword-day extraction.
/// </summary>
[TestFixture]
public sealed class RecoverySnapshotBuilderIneligibleTests
{
    private static readonly Guid AgentA = new("00000000-0000-0000-0000-0000000000a1");
    private static readonly Guid AgentB = new("00000000-0000-0000-0000-0000000000a2");
    private static readonly Guid Shift1 = new("00000000-0000-0000-0000-000000000101");
    private static readonly Guid Shift2 = new("00000000-0000-0000-0000-000000000102");
    private static readonly DateOnly KeywordDay = new(2026, 6, 10);
    private static readonly DateOnly OpenDay = new(2026, 6, 11);

    private static readonly ScheduleCommandKeywordSet DefaultKeywords = ScheduleCommandKeywordTestFactory.Default;

    private static readonly IReadOnlyDictionary<string, Klacks.ScheduleOptimizer.Models.ScheduleCommandKeyword> DefaultKeywordMap =
        ScheduleCommandKeywordMapper.BuildMap(DefaultKeywords);

    [Test]
    public void AvailabilityVeto_OnKeywordDay_IsKept()
    {
        var availability = new[] { new IneligibleKey(AgentA, Shift1, KeywordDay) };

        var result = RecoverySnapshotBuilder.MergeIneligible([], availability);

        result.ShouldContain(new IneligibleKey(AgentA, Shift1, KeywordDay));
    }

    [Test]
    public void AvailabilityVeto_OnDayWithoutKeyword_IsKept()
    {
        var availability = new[] { new IneligibleKey(AgentA, Shift1, OpenDay) };

        var result = RecoverySnapshotBuilder.MergeIneligible([], availability);

        result.ShouldContain(new IneligibleKey(AgentA, Shift1, OpenDay));
    }

    [Test]
    public void QualificationAndAvailability_AreBothKept()
    {
        var qualification = new[] { new IneligibleKey(AgentA, Shift1, KeywordDay) };
        var availability = new[] { new IneligibleKey(AgentA, Shift2, KeywordDay), new IneligibleKey(AgentB, Shift1, KeywordDay) };

        var result = RecoverySnapshotBuilder.MergeIneligible(qualification, availability);

        result.ShouldBe(
            [
                new IneligibleKey(AgentA, Shift1, KeywordDay),
                new IneligibleKey(AgentA, Shift2, KeywordDay),
                new IneligibleKey(AgentB, Shift1, KeywordDay),
            ],
            ignoreOrder: true);
    }

    [Test]
    public void ExtractKeywordDays_RecognizesMappedKeywords_AndIgnoresUnmapped()
    {
        var commands = new List<ScheduleCommand>
        {
            new() { ClientId = AgentA, CurrentDate = KeywordDay, CommandKeyword = "LATE" },
            new() { ClientId = AgentA, CurrentDate = OpenDay, CommandKeyword = "FREE" },
            new() { ClientId = AgentB, CurrentDate = KeywordDay, CommandKeyword = "not-a-keyword" },
        };

        var days = RecoverySnapshotBuilder.ExtractKeywordDays(commands, DefaultKeywordMap);

        days.ShouldContainKey((AgentA, KeywordDay));
        days.ShouldContainKey((AgentA, OpenDay));
        days.ShouldNotContainKey((AgentB, KeywordDay));
    }

    [Test]
    public void ExtractKeywordDays_KeepsTheKeywordItself_NotJustTheDay()
    {
        // The day alone only suppressed the availability veto. The category gates need to know WHICH
        // keyword governs the day, otherwise an ONLY-LATE restriction stays invisible to recovery.
        var commands = new List<ScheduleCommand>
        {
            new() { ClientId = AgentA, CurrentDate = KeywordDay, CommandKeyword = "LATE" },
        };

        var days = RecoverySnapshotBuilder.ExtractKeywordDays(commands, DefaultKeywordMap);

        days[(AgentA, KeywordDay)].ShouldBe(ScheduleCommandKeyword.OnlyLate);
    }

    [Test]
    public void ExtractKeywordDays_SeveralCommandsOnOneDay_FreeWins()
    {
        // Otherwise the answer would depend on the order the rows came back in.
        var commands = new List<ScheduleCommand>
        {
            new() { ClientId = AgentA, CurrentDate = KeywordDay, CommandKeyword = "LATE" },
            new() { ClientId = AgentA, CurrentDate = KeywordDay, CommandKeyword = "FREE" },
        };

        var days = RecoverySnapshotBuilder.ExtractKeywordDays(commands, DefaultKeywordMap);

        days[(AgentA, KeywordDay)].ShouldBe(ScheduleCommandKeyword.Free);
    }

    private static ScheduleCommandKeyword ExtractOne(params string[] keywords)
    {
        var commands = keywords
            .Select(k => new ScheduleCommand { ClientId = AgentA, CurrentDate = KeywordDay, CommandKeyword = k })
            .ToList();

        return RecoverySnapshotBuilder.ExtractKeywordDays(commands, DefaultKeywordMap)[(AgentA, KeywordDay)];
    }

    [Test]
    public void ExtractKeywordDays_NoEarlyAndNoNight_LeavesOnlyLate()
    {
        ExtractOne("-EARLY", "-NIGHT").ShouldBe(ScheduleCommandKeyword.OnlyLate);
        ExtractOne("-NIGHT", "-EARLY").ShouldBe(ScheduleCommandKeyword.OnlyLate);
    }

    [Test]
    public void ExtractKeywordDays_NotFreeAndNoNight_KeepsNoNight()
    {
        ExtractOne("-FREE", "-NIGHT").ShouldBe(ScheduleCommandKeyword.NoNight);
    }

    [Test]
    public void ExtractKeywordDays_OnlyEarlyAndOnlyLate_ClosesDay()
    {
        ExtractOne("EARLY", "LATE").ShouldBe(ScheduleCommandKeyword.Free);
    }

    [Test]
    public void ExtractKeywordDays_OnlyNotFree_StaysAKeywordDay()
    {
        ExtractOne("-FREE").ShouldBe(ScheduleCommandKeyword.NotFree);
    }
}
