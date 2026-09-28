// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the orchestration of the analyzer: run days come from the wizard shift builder (never called
/// with an empty shift list), contracts from the range provider, shifts without run day are dropped
/// from findings, proposals and names, a client without contract yields F1 and F5, and the period is
/// validated.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Interfaces.Grouping;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.ScheduleOptimizer.Models;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class GroupingFeasibilityAnalyzerTests
{
    private static readonly DateOnly Monday = new(2026, 6, 15);
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly Guid ShiftWithoutRunDayId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();

    private IGroupingFeasibilityDataSource _dataSource = null!;
    private IWizardShiftBuilder _shiftBuilder = null!;
    private IClientContractDataProvider _contracts = null!;
    private ISettingsReader _settings = null!;
    private ICompanyClock _clock = null!;
    private GroupingFeasibilityAnalyzer _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _dataSource = Substitute.For<IGroupingFeasibilityDataSource>();
        _shiftBuilder = Substitute.For<IWizardShiftBuilder>();
        _contracts = Substitute.For<IClientContractDataProvider>();
        _settings = Substitute.For<ISettingsReader>();
        _clock = Substitute.For<ICompanyClock>();
        _clock.GetTodayDateAsync(Arg.Any<CancellationToken>()).Returns(Monday);
        _sut = new GroupingFeasibilityAnalyzer(
            _dataSource, _shiftBuilder, _contracts, _settings, _clock, NullLogger<GroupingFeasibilityAnalyzer>.Instance);
    }

    private void GivenSnapshot(bool withShift = true, bool withShiftWithoutRunDay = false)
    {
        var shifts = new List<GroupingShiftRecord>();
        if (withShift)
        {
            shifts.Add(new GroupingShiftRecord(ShiftId, "Early", new TimeOnly(6, 0), new TimeOnly(14, 0), 1));
        }

        if (withShiftWithoutRunDay)
        {
            shifts.Add(new GroupingShiftRecord(ShiftWithoutRunDayId, "Weekend", new TimeOnly(6, 0), new TimeOnly(14, 0), 1));
        }

        var memberships = new List<GroupingMembershipRecord>
        {
            new(Guid.NewGuid(), GroupId, ClientId, null),
        };
        if (withShift)
        {
            memberships.Add(new GroupingMembershipRecord(Guid.NewGuid(), GroupId, null, ShiftId));
        }

        _dataSource.LoadAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new GroupingFeasibilitySnapshot(
                [new GroupingGroupRecord(GroupId, "Unit", null, null, null, null)],
                memberships,
                [new GroupingClientRecord(ClientId, "Anna Muster", null, null)],
                shifts,
                [],
                [],
                new HashSet<GroupingEntityPair>(),
                new List<ClientAvailability>(),
                new HashSet<GroupingEntityPair>()));

        _shiftBuilder.BuildAsync(Arg.Any<IReadOnlyList<Guid>?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), null, Arg.Any<CancellationToken>())
            .Returns(new List<CoreShift> { new(ShiftId.ToString(), "Early", "2026-06-15", "06:00", "14:00", 8, 1, 0) });
    }

    private void GivenContract(bool active)
    {
        _contracts.GetEffectiveContractDataForClientsRangeAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<DateOnly, Dictionary<Guid, EffectiveContractData>>
            {
                [Monday] = new() { [ClientId] = new EffectiveContractData { HasActiveContract = active, PerformsShiftWork = true } }
            });
    }

    [Test]
    public async Task EligibleClientInTheShiftsGroup_ProducesNoFindings()
    {
        GivenSnapshot();
        GivenContract(active: true);

        var report = await _sut.AnalyzeAsync(new GroupingAnalysisRequest(Monday, Monday.AddDays(7), null), CancellationToken.None);

        report.Findings.ShouldBeEmpty();
        report.Proposals.ShouldBeEmpty();
        report.HasReportFindings.ShouldBeFalse();
        report.AnalysedShiftCount.ShouldBe(1);
    }

    [Test]
    public async Task ClientWithoutContract_YieldsUnfillableShiftAndClientWithoutFit()
    {
        GivenSnapshot();
        GivenContract(active: false);

        var report = await _sut.AnalyzeAsync(new GroupingAnalysisRequest(Monday, Monday.AddDays(7), null), CancellationToken.None);

        report.Findings.ShouldContain(f => f.Code == GroupingFindingCode.ShiftUnfillableGlobally && f.Reason == GroupingIneligibilityReason.NoActiveContract);
        report.Findings.ShouldContain(f => f.Code == GroupingFindingCode.ClientFitsNoShift);
        report.HasReportFindings.ShouldBeTrue();
        report.ReportFingerprint.Length.ShouldBe(64);
    }

    [Test]
    public async Task NoShifts_NeverCallsTheShiftBuilder()
    {
        GivenSnapshot(withShift: false);
        GivenContract(active: true);

        var report = await _sut.AnalyzeAsync(new GroupingAnalysisRequest(Monday, Monday.AddDays(7), null), CancellationToken.None);

        await _shiftBuilder.DidNotReceiveWithAnyArgs().BuildAsync(default, default, default, default, default);
        report.AnalysedShiftCount.ShouldBe(0);
    }

    [Test]
    public async Task ShiftWithoutRunDaySlot_IsReferencedByNoFindingAndNoProposal()
    {
        GivenSnapshot(withShiftWithoutRunDay: true);
        GivenContract(active: true);

        var report = await _sut.AnalyzeAsync(new GroupingAnalysisRequest(Monday, Monday.AddDays(7), null), CancellationToken.None);

        await _shiftBuilder.Received(1).BuildAsync(
            Arg.Is<IReadOnlyList<Guid>?>(ids => ids != null && ids.Contains(ShiftId) && ids.Contains(ShiftWithoutRunDayId)),
            Arg.Any<DateOnly>(),
            Arg.Any<DateOnly>(),
            null,
            Arg.Any<CancellationToken>());
        report.Findings.ShouldNotContain(f => f.ShiftId == ShiftWithoutRunDayId);
        report.Proposals.ShouldNotContain(p => p.ShiftId == ShiftWithoutRunDayId);
        report.ShiftNames.ShouldNotContainKey(ShiftWithoutRunDayId);
        report.AnalysedShiftCount.ShouldBe(1);
    }

    [Test]
    public async Task PeriodLongerThanTheMaximum_IsRejected()
    {
        await Should.ThrowAsync<ArgumentOutOfRangeException>(() =>
            _sut.AnalyzeAsync(new GroupingAnalysisRequest(Monday, Monday.AddDays(400), null), CancellationToken.None));
    }

    [Test]
    public async Task ChildGroupWithoutNestedSetRoot_GetsItsLineageOverParent_AndMembersTheirDirectGroups()
    {
        var rootId = Guid.NewGuid();
        var childId = Guid.NewGuid();
        _dataSource.LoadAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new GroupingFeasibilitySnapshot(
                [
                    new GroupingGroupRecord(rootId, "Region", null, null, null, null),
                    new GroupingGroupRecord(childId, "Ward", rootId, null, null, null),
                ],
                [
                    new GroupingMembershipRecord(Guid.NewGuid(), childId, ClientId, null),
                    new GroupingMembershipRecord(Guid.NewGuid(), childId, null, ShiftId),
                ],
                [new GroupingClientRecord(ClientId, "Anna Muster", null, null)],
                [new GroupingShiftRecord(ShiftId, "Early", new TimeOnly(6, 0), new TimeOnly(14, 0), 1)],
                [],
                [],
                new HashSet<GroupingEntityPair>(),
                new List<ClientAvailability>(),
                new HashSet<GroupingEntityPair>()));
        _shiftBuilder.BuildAsync(Arg.Any<IReadOnlyList<Guid>?>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), null, Arg.Any<CancellationToken>())
            .Returns(new List<CoreShift> { new(ShiftId.ToString(), "Early", "2026-06-15", "06:00", "14:00", 8, 1, 0) });
        GivenContract(active: true);

        var report = await _sut.AnalyzeAsync(new GroupingAnalysisRequest(Monday, Monday.AddDays(7), null), CancellationToken.None);

        report.GroupLineage[childId].ShouldBe([childId, rootId]);
        report.GroupLineage[rootId].ShouldBe([rootId]);
        report.MemberGroups[ClientId].ShouldBe([childId]);
        report.MemberGroups[ShiftId].ShouldBe([childId]);
    }

    [Test]
    public async Task ClientWithoutContract_IsNamedInTheUnitSummaryAsTheDominantCause()
    {
        GivenSnapshot();
        GivenContract(active: false);

        var report = await _sut.AnalyzeAsync(new GroupingAnalysisRequest(Monday, Monday.AddDays(7), null), CancellationToken.None);

        var unit = report.UnitSummaries.ShouldHaveSingleItem();
        unit.UnitId.ShouldBe(GroupId);
        unit.DutiesAnalysed.ShouldBe(1);
        unit.DutiesUnfillableGlobally.ShouldBe(1);
        unit.EmployeesInScope.ShouldBe(1);
        unit.EmployeesWithoutContract.ShouldBe(1);
        unit.DominantReason.ShouldBe(GroupingIneligibilityReason.NoActiveContract);
    }

    [Test]
    public async Task EligibleClient_LeavesTheUnitWithoutGaps()
    {
        GivenSnapshot();
        GivenContract(active: true);

        var report = await _sut.AnalyzeAsync(new GroupingAnalysisRequest(Monday, Monday.AddDays(7), null), CancellationToken.None);

        report.UnitSummaries.ShouldHaveSingleItem().HasGaps.ShouldBeFalse();
    }
}
