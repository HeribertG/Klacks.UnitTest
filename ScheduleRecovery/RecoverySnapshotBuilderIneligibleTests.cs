// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Schedules.Recovery;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.ScheduleRecovery.Model;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Unit tests for the precedence logic of <see cref="RecoverySnapshotBuilder"/>: the schedule hierarchy
/// Break &gt; Keyword &gt; Availability is applied so that an availability veto is dropped on a keyword day
/// (the keyword governs), while qualification ineligibility is sharp on every day and never suppressed.
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

    private static HashSet<(Guid AgentId, DateOnly Date)> KeywordDays(params (Guid, DateOnly)[] days)
        => new(days);

    [Test]
    public void AvailabilityVeto_OnKeywordDay_IsSuppressed()
    {
        var availability = new[] { new IneligibleKey(AgentA, Shift1, KeywordDay) };

        var result = RecoverySnapshotBuilder.MergeIneligible(
            [], availability, KeywordDays((AgentA, KeywordDay)));

        result.ShouldNotContain(new IneligibleKey(AgentA, Shift1, KeywordDay));
        result.ShouldBeEmpty();
    }

    [Test]
    public void AvailabilityVeto_OnDayWithoutKeyword_IsKept()
    {
        var availability = new[] { new IneligibleKey(AgentA, Shift1, OpenDay) };

        var result = RecoverySnapshotBuilder.MergeIneligible(
            [], availability, KeywordDays((AgentA, KeywordDay)));

        result.ShouldContain(new IneligibleKey(AgentA, Shift1, OpenDay));
    }

    [Test]
    public void QualificationVeto_OnKeywordDay_StillExcludes()
    {
        // The linchpin: qualification is outside the hierarchy and must survive keyword-day suppression.
        var qualification = new[] { new IneligibleKey(AgentA, Shift1, KeywordDay) };

        var result = RecoverySnapshotBuilder.MergeIneligible(
            qualification, [], KeywordDays((AgentA, KeywordDay)));

        result.ShouldContain(new IneligibleKey(AgentA, Shift1, KeywordDay));
    }

    [Test]
    public void OnKeywordDay_QualificationStays_WhileAvailabilityDrops()
    {
        var qualification = new[] { new IneligibleKey(AgentA, Shift1, KeywordDay) };
        var availability = new[] { new IneligibleKey(AgentA, Shift2, KeywordDay) };

        var result = RecoverySnapshotBuilder.MergeIneligible(
            qualification, availability, KeywordDays((AgentA, KeywordDay)));

        result.ShouldContain(new IneligibleKey(AgentA, Shift1, KeywordDay));
        result.ShouldNotContain(new IneligibleKey(AgentA, Shift2, KeywordDay));
    }

    [Test]
    public void KeywordDay_OfOneAgent_DoesNotSuppressAnotherAgent()
    {
        var availability = new[] { new IneligibleKey(AgentB, Shift1, KeywordDay) };

        var result = RecoverySnapshotBuilder.MergeIneligible(
            [], availability, KeywordDays((AgentA, KeywordDay)));

        result.ShouldContain(new IneligibleKey(AgentB, Shift1, KeywordDay));
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

        var days = RecoverySnapshotBuilder.ExtractKeywordDays(commands);

        days.ShouldContain((AgentA, KeywordDay));
        days.ShouldContain((AgentA, OpenDay));
        days.ShouldNotContain((AgentB, KeywordDay));
    }
}
