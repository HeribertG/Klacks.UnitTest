// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.ScheduleOptimizer.Models;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public sealed class QualificationGapReportBuilderTests
{
    private static readonly Guid ShiftId = Guid.Parse("55555555-5555-5555-5555-555555555555");
    private static readonly Guid Qual = Guid.Parse("99999999-9999-9999-9999-999999999999");
    private static readonly DateOnly Date = new(2026, 6, 10);

    private static EligibilityMatrix Matrix(params (string AgentId, Guid ShiftId, DateOnly Date)[] ineligible)
    {
        var set = new HashSet<(string, Guid, DateOnly)>(ineligible);
        var gaps = ineligible.ToDictionary(
            k => k,
            _ => (IReadOnlyList<QualificationGap>)new List<QualificationGap>
            {
                new(Qual, QualificationGapReason.Missing, QualificationLevel.Proficient),
            });
        return new EligibilityMatrix
        {
            Ineligible = set,
            Gaps = gaps,
            QualificationInfo = new Dictionary<Guid, QualificationInfo>
            {
                [Qual] = new QualificationInfo(new MultiLanguage { De = "Pflege" }, "🩺"),
            },
            ShiftNames = new Dictionary<Guid, string> { [ShiftId] = "Care" },
        };
    }

    private static EligibilityMatrix MatrixWithGap(string agentId, QualificationGapReason reason, bool isMandatory)
    {
        var gap = new QualificationGap(Qual, reason, QualificationLevel.Proficient, isMandatory);
        var key = (agentId, ShiftId, Date);
        var ineligible = new HashSet<(string, Guid, DateOnly)>();
        if (gap.Severity == QualificationGapSeverity.Error)
        {
            ineligible.Add(key);
        }

        return new EligibilityMatrix
        {
            Ineligible = ineligible,
            Gaps = new Dictionary<(string, Guid, DateOnly), IReadOnlyList<QualificationGap>> { [key] = new List<QualificationGap> { gap } },
            QualificationInfo = new Dictionary<Guid, QualificationInfo> { [Qual] = new QualificationInfo(new MultiLanguage { De = "Pflege" }, "🩺") },
            ShiftNames = new Dictionary<Guid, string> { [ShiftId] = "Care" },
        };
    }

    private static CoreShift Slot() => new(
        Id: ShiftId.ToString(),
        Name: "Care",
        Date: Date.ToString("yyyy-MM-dd"),
        StartTime: "07:00",
        EndTime: "15:00",
        Hours: 8,
        RequiredAssignments: 1,
        Priority: 0);

    private static CoreAgent Agent(string id) => new(
        Id: id,
        CurrentHours: 0,
        GuaranteedHours: 0,
        MaxConsecutiveDays: 6,
        MinRestHours: 11,
        Motivation: 0.5,
        MaxDailyHours: 10,
        MaxWeeklyHours: 50,
        MaxOptimalGap: 2);

    [Test]
    public void Unfillable_EmptySlot_AllAgentsIneligible_Reported()
    {
        var matrix = Matrix(("A", ShiftId, Date), ("B", ShiftId, Date));

        var report = QualificationGapReportBuilder.BuildUnfillableSlots(
            matrix, [Slot()], [Agent("A"), Agent("B")], finalTokens: []);

        report.ShouldContain(g => g.Kind == QualificationGapKind.UnfillableSlot && g.ShiftId == ShiftId);
    }

    [Test]
    public void Unfillable_EmptySlot_OneAgentEligible_NotReported()
    {
        // B is eligible, so the empty slot is ordinary under-supply — not a qualification gap.
        var matrix = Matrix(("A", ShiftId, Date));

        var report = QualificationGapReportBuilder.BuildUnfillableSlots(
            matrix, [Slot()], [Agent("A"), Agent("B")], finalTokens: []);

        report.ShouldBeEmpty();
    }

    [Test]
    public void Unfillable_SlotFilled_NotReported()
    {
        var matrix = Matrix(("A", ShiftId, Date), ("B", ShiftId, Date));
        var token = new CoreToken(
            WorkIds: [], ShiftTypeIndex: 0, Date: Date, TotalHours: 8,
            StartAt: Date.ToDateTime(new TimeOnly(7, 0)), EndAt: Date.ToDateTime(new TimeOnly(15, 0)),
            BlockId: Guid.NewGuid(), PositionInBlock: 0, IsLocked: false, LocationContext: null,
            ShiftRefId: ShiftId, AgentId: "C");

        var report = QualificationGapReportBuilder.BuildUnfillableSlots(
            matrix, [Slot()], [Agent("A"), Agent("B")], finalTokens: [token]);

        report.ShouldBeEmpty();
    }

    [Test]
    public void AssignedUnqualified_UnqualifiedAgentInPlan_Reported()
    {
        var matrix = Matrix(("A", ShiftId, Date));

        var report = QualificationGapReportBuilder.BuildAssignedUnqualified(
            matrix, new[] { ("A", "Anna", ShiftId, Date) });

        report.ShouldContain(g =>
            g.Kind == QualificationGapKind.AssignedUnqualified && g.AgentName == "Anna" && g.ShiftId == ShiftId);
    }

    [Test]
    public void AssignedUnqualified_QualifiedAgent_NotReported()
    {
        var matrix = Matrix(("A", ShiftId, Date));

        var report = QualificationGapReportBuilder.BuildAssignedUnqualified(
            matrix, new[] { ("B", "Bob", ShiftId, Date) });

        report.ShouldBeEmpty();
    }

    [Test]
    public void AssignedUnqualified_WarningGap_ReportedAsWarning()
    {
        // A too-low-level (Warning) agent the wizard assigned must surface in the report.
        var matrix = MatrixWithGap("A", QualificationGapReason.InsufficientLevel, isMandatory: true);

        var report = QualificationGapReportBuilder.BuildAssignedUnqualified(
            matrix, new[] { ("A", "Anna", ShiftId, Date) });

        report.ShouldContain(g => g.Severity == QualificationGapSeverity.Warning && g.AgentName == "Anna");
    }

    [Test]
    public void Unfillable_OnlyWarningGaps_NotReportedAsUnfillable()
    {
        // Every agent only has a Warning gap → the slot is fillable (they could take it), so an
        // empty slot here is ordinary under-supply, never an unfillable-qualification report.
        var matrix = MatrixWithGap("A", QualificationGapReason.InsufficientLevel, isMandatory: true);

        var report = QualificationGapReportBuilder.BuildUnfillableSlots(
            matrix, [Slot()], [Agent("A")], finalTokens: []);

        report.ShouldBeEmpty();
    }
}
