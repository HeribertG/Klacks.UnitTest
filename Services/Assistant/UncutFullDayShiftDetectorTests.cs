// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for UncutFullDayShiftDetector -- covers empty-result, severity-by-proximity
/// (High/Medium/Low, including an already-started duty), the SplitShift/unequal-times/already-
/// ended/container-shift negative cases, scenario-clone exclusion, soft-delete exclusion,
/// DedupKey stability, that a large backlog of long-past duties cannot crowd a genuinely upcoming
/// one out of the per-tick emission cap, and the rotation that decides which candidates fill that
/// cap when many of them tie on proximity to today -- never-opened candidates first, then the open
/// ledger rows least recently observed. The container-shift exclusion pins the design decision that
/// "cut" is a task-level concept: a container with equal StartShift/EndShift is exclusively
/// EmptyContainerDetector's concern, never this detector's.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class UncutFullDayShiftDetectorTests
{
    private static readonly DateTime FirstTickInstant = new(2026, 9, 13, 6, 0, 0, DateTimeKind.Utc);

    private const int RotationBacklogOverflow = 5;

    private IShiftRepository _shiftRepository = null!;
    private IShiftGroupScopeReader _groupScopeReader = null!;
    private IAgentConditionRepository _agentConditionRepository = null!;
    private UncutFullDayShiftDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _groupScopeReader = ShiftGroupScopeReaderStub.WithoutAnyGroups();
        _agentConditionRepository = Substitute.For<IAgentConditionRepository>();
        SetOpenLedgerRows();
        _sut = new UncutFullDayShiftDetector(
            _shiftRepository, _groupScopeReader, _agentConditionRepository,
            new FixedCompanyClock(DateTimeOffset.UtcNow), NullLogger<UncutFullDayShiftDetector>.Instance);
    }

    /// <summary>
    /// Stubs the ledger rows GetOpenByKindAsync returns for this detector's Kind -- an empty list by
    /// default (nothing opened yet), or one row per (shiftId, lastSeenAtUtc) pair passed in, mirroring
    /// what AgentConditionLedgerService.UpsertDetectedAsync leaves behind after a real tick.
    /// </summary>
    private void SetOpenLedgerRows(params (Guid ShiftId, DateTime LastSeenAtUtc)[] openRows)
    {
        _agentConditionRepository.GetOpenByKindAsync(AgentTriggerKinds.UncutFulldayShift, Arg.Any<CancellationToken>())
            .Returns(openRows
                .Select(row => new AgentCondition { EntityId = row.ShiftId, LastSeenAtUtc = row.LastSeenAtUtc })
                .ToList());
    }

    private static Shift MakeUncutFullDayShift(
        DateOnly fromDate,
        DateOnly? untilDate = null,
        ShiftStatus status = ShiftStatus.OriginalShift,
        TimeOnly? startShift = null,
        TimeOnly? endShift = null,
        Guid? analyseToken = null,
        Guid? scenarioSourceShiftId = null,
        bool isDeleted = false,
        Guid? id = null,
        ShiftType shiftType = ShiftType.IsTask)
    {
        var time = startShift ?? new TimeOnly(7, 0);
        return new Shift
        {
            Id = id ?? Guid.NewGuid(),
            Name = "24h-Schichtdienst",
            Abbreviation = "24H",
            Status = status,
            ShiftType = shiftType,
            FromDate = fromDate,
            UntilDate = untilDate,
            StartShift = time,
            EndShift = endShift ?? time,
            AnalyseToken = analyseToken,
            ScenarioSourceShiftId = scenarioSourceShiftId,
            IsDeleted = isDeleted
        };
    }

    private void SetupQuery(params Shift[] shifts)
    {
        _shiftRepository.GetQuery().Returns(new TestAsyncEnumerable<Shift>(shifts.ToList()));
    }

    private async Task<List<Guid>> ReportedShiftIdsAsync() =>
        (await _sut.DetectAsync()).Cast<UncutFullDayShiftTriggerEvent>().Select(e => e.ShiftId).ToList();

    [Test]
    public async Task DetectAsync_EmptyDatabase_ReturnsEmpty()
    {
        SetupQuery();

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_UncutFullDayShiftIn5Days_HasHighSeverity()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeUncutFullDayShift(today.AddDays(5));
        SetupQuery(shift);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var e = events.Single() as UncutFullDayShiftTriggerEvent;
        Assert.That(e!.DaysUntil, Is.EqualTo(5));
        Assert.That(e.Severity, Is.EqualTo(AgentTriggerSeverity.High));
    }

    [Test]
    public async Task DetectAsync_UncutFullDayShiftIn20Days_HasMediumSeverity()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeUncutFullDayShift(today.AddDays(20));
        SetupQuery(shift);

        var events = await _sut.DetectAsync();

        var e = events.Single() as UncutFullDayShiftTriggerEvent;
        Assert.That(e!.Severity, Is.EqualTo(AgentTriggerSeverity.Medium));
    }

    [Test]
    public async Task DetectAsync_UncutFullDayShiftIn60Days_HasLowSeverity()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeUncutFullDayShift(today.AddDays(60));
        SetupQuery(shift);

        var events = await _sut.DetectAsync();

        var e = events.Single() as UncutFullDayShiftTriggerEvent;
        Assert.That(e!.Severity, Is.EqualTo(AgentTriggerSeverity.Low));
    }

    [Test]
    public async Task DetectAsync_AlreadyStartedOngoingUncutFullDayShift_HasHighSeverity()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeUncutFullDayShift(today.AddDays(-30), untilDate: null);
        SetupQuery(shift);

        var events = await _sut.DetectAsync();

        Assert.That(events, Has.Count.EqualTo(1));
        var e = events.Single() as UncutFullDayShiftTriggerEvent;
        Assert.That(e!.DaysUntil, Is.EqualTo(-30));
        Assert.That(e.Severity, Is.EqualTo(AgentTriggerSeverity.High));
    }

    [Test]
    public async Task DetectAsync_AlreadyEndedShift_IsExcluded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeUncutFullDayShift(today.AddDays(-30), untilDate: today.AddDays(-1));
        SetupQuery(shift);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_AlreadySplitShift_IsExcluded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeUncutFullDayShift(today.AddDays(5), status: ShiftStatus.SplitShift);
        SetupQuery(shift);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ContainerShiftWithEqualTimes_IsExcluded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeUncutFullDayShift(today.AddDays(5), shiftType: ShiftType.IsContainer);
        SetupQuery(shift);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_DifferentStartAndEndTimes_IsExcluded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeUncutFullDayShift(today.AddDays(5), startShift: new TimeOnly(7, 0), endShift: new TimeOnly(19, 0));
        SetupQuery(shift);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ScenarioCloneViaAnalyseToken_IsExcluded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeUncutFullDayShift(today.AddDays(5), analyseToken: Guid.NewGuid());
        SetupQuery(shift);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_ScenarioCloneViaScenarioSourceShiftId_IsExcluded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeUncutFullDayShift(today.AddDays(5), scenarioSourceShiftId: Guid.NewGuid());
        SetupQuery(shift);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_SoftDeletedShift_IsExcluded()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeUncutFullDayShift(today.AddDays(5), isDeleted: true);
        SetupQuery(shift);

        var events = await _sut.DetectAsync();

        Assert.That(events, Is.Empty);
    }

    [Test]
    public async Task DetectAsync_DedupKey_IsShiftIdOnly_NotDateDependent()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shiftId = Guid.NewGuid();
        var shift = MakeUncutFullDayShift(today.AddDays(5), id: shiftId);
        SetupQuery(shift);

        var events = await _sut.DetectAsync();

        var e = events.Single() as UncutFullDayShiftTriggerEvent;
        Assert.That(e!.DedupKey, Is.EqualTo(shiftId.ToString()));
    }

    [Test]
    public async Task DetectAsync_LargeBacklogOfAncientDuties_DoesNotStarveOutUpcomingShift()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var ancientShifts = Enumerable.Range(0, UncutFullDayShiftDetector.MaxFindingsPerTick + 5)
            .Select(i => MakeUncutFullDayShift(today.AddDays(-1000 - i), untilDate: null))
            .ToArray();
        var upcomingShiftId = Guid.NewGuid();
        var upcomingShift = MakeUncutFullDayShift(today.AddDays(3), id: upcomingShiftId);
        SetupQuery(ancientShifts.Append(upcomingShift).ToArray());

        var events = (await _sut.DetectAsync()).Cast<UncutFullDayShiftTriggerEvent>().ToList();

        Assert.That(events, Has.Count.EqualTo(UncutFullDayShiftDetector.MaxFindingsPerTick));
        Assert.That(events.Any(e => e.ShiftId == upcomingShiftId), Is.True);
        Assert.That(events.Single(e => e.ShiftId == upcomingShiftId).Severity, Is.EqualTo(AgentTriggerSeverity.High));
    }

    [Test]
    public async Task DetectAsync_BacklogSharesOneProximityAndLedgerIsEmpty_FirstTickFillsTheCap()
    {
        // Every candidate ties on |DaysUntil| -- the shape a bulk-created backlog actually has, and the
        // one a plain proximity cap degenerates on. With nothing open in the ledger yet, every candidate
        // is in the never-opened group, so the cap alone decides how many are reported.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var backlogSize = UncutFullDayShiftDetector.MaxFindingsPerTick + RotationBacklogOverflow;
        var backlog = Enumerable.Range(0, backlogSize)
            .Select(_ => MakeUncutFullDayShift(today.AddDays(100)))
            .ToArray();
        SetupQuery(backlog);

        var reported = await ReportedShiftIdsAsync();

        Assert.That(reported, Has.Count.EqualTo(UncutFullDayShiftDetector.MaxFindingsPerTick));
        Assert.That(reported, Is.SubsetOf(backlog.Select(s => s.Id)));
        Assert.That(reported.Distinct().Count(), Is.EqualTo(reported.Count));
    }

    [Test]
    public async Task DetectAsync_AfterTheFirstTickWasObserved_SecondTickLeadsWithTheNeverReportedRest()
    {
        // What a fixed proximity slice could never do: once the first tick's findings carry an open
        // ledger row, the rotation puts the candidates that were never opened at the front, and fills the
        // rest of the cap with the longest-unobserved of the already reported ones -- so a recipient who
        // was throttled on tick 1 is offered them again instead of losing them forever.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var backlogSize = UncutFullDayShiftDetector.MaxFindingsPerTick + RotationBacklogOverflow;
        var backlog = Enumerable.Range(0, backlogSize)
            .Select(_ => MakeUncutFullDayShift(today.AddDays(100)))
            .ToArray();
        var backlogIds = backlog.Select(s => s.Id).ToList();
        SetupQuery(backlog);

        var firstTick = await ReportedShiftIdsAsync();
        SetOpenLedgerRows(firstTick.Select(id => (id, FirstTickInstant)).ToArray());
        var starved = backlogIds.Except(firstTick).ToList();

        var secondTick = await ReportedShiftIdsAsync();

        Assert.That(starved, Has.Count.EqualTo(RotationBacklogOverflow));
        Assert.That(secondTick, Has.Count.EqualTo(UncutFullDayShiftDetector.MaxFindingsPerTick));
        Assert.That(secondTick.Take(RotationBacklogOverflow), Is.EquivalentTo(starved),
            "the shifts no tick has ever opened a ledger row for must lead the second tick");
        Assert.That(secondTick.Skip(RotationBacklogOverflow), Is.SubsetOf(firstTick));
    }

    [Test]
    public async Task DetectAsync_EveryCandidateOpenWithStaggeredLastSeen_ReportsTheLongestUnobservedOnes()
    {
        // No never-opened candidates left at all: the whole selection is then the LastSeenAtUtc order,
        // oldest observation first, which is what makes the backlog cycle instead of stalling.
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var backlogSize = UncutFullDayShiftDetector.MaxFindingsPerTick + RotationBacklogOverflow;
        var backlog = Enumerable.Range(0, backlogSize)
            .Select(_ => MakeUncutFullDayShift(today.AddDays(100)))
            .ToArray();
        var backlogIds = backlog.Select(s => s.Id).ToList();
        SetupQuery(backlog);
        SetOpenLedgerRows(backlogIds
            .Select((id, index) => (id, FirstTickInstant.AddMinutes(index)))
            .ToArray());

        var reported = await ReportedShiftIdsAsync();

        Assert.That(reported, Is.EqualTo(backlogIds.Take(UncutFullDayShiftDetector.MaxFindingsPerTick).ToList()));
    }

    [Test]
    public async Task DetectAsync_FewerCandidatesThanTheCap_ReportsAllOfThemInBusinessOrder()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shifts = Enumerable.Range(0, 5)
            .Select(offset => MakeUncutFullDayShift(today.AddDays(offset + 1)))
            .ToList();
        SetupQuery(shifts.AsEnumerable().Reverse().ToArray());

        var reported = await ReportedShiftIdsAsync();

        Assert.That(reported, Is.EqualTo(shifts.Select(s => s.Id).ToList()));
    }

    [Test]
    public async Task DetectAsync_ShiftInOneGroup_CarriesThatGroup_AndPreselectsItInTheActionParams()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeUncutFullDayShift(today.AddDays(3));
        var groupId = Guid.NewGuid();
        SetupQuery(shift);
        ShiftGroupScopeReaderStub.SetGroups(_groupScopeReader, (shift.Id, new[] { groupId }));

        var uncut = (UncutFullDayShiftTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(uncut.GroupIds, Is.EqualTo(new[] { groupId }));
        Assert.That(uncut.ActionParams![ProactiveActionParamKeys.GroupId], Is.EqualTo(groupId.ToString()));
        Assert.That(uncut.RequiresGroupScope, Is.True);
    }

    [Test]
    public async Task DetectAsync_ShiftInTwoGroups_CarriesBoth_NotOnlyTheFirst()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeUncutFullDayShift(today.AddDays(3));
        var firstGroupId = Guid.NewGuid();
        var secondGroupId = Guid.NewGuid();
        SetupQuery(shift);
        ShiftGroupScopeReaderStub.SetGroups(_groupScopeReader, (shift.Id, new[] { firstGroupId, secondGroupId }));

        var uncut = (UncutFullDayShiftTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(uncut.GroupIds, Is.EquivalentTo(new[] { firstGroupId, secondGroupId }));
    }

    [Test]
    public async Task DetectAsync_ShiftWithoutAnyGroup_CarriesNoGroup_AndOmitsTheGroupActionParam()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shift = MakeUncutFullDayShift(today.AddDays(3));
        SetupQuery(shift);

        var uncut = (UncutFullDayShiftTriggerEvent)(await _sut.DetectAsync()).Single();

        Assert.That(uncut.GroupIds, Is.Empty);
        Assert.That(uncut.ActionParams!.ContainsKey(ProactiveActionParamKeys.GroupId), Is.False);
        Assert.That(uncut.RequiresGroupScope, Is.True);
    }

    [Test]
    public async Task DetectAsync_ManyShifts_ResolvesGroupsInOneBatchedLookup_NeverOnePerShift()
    {
        var today = DateOnly.FromDateTime(DateTime.UtcNow);
        var shifts = Enumerable.Range(0, 10).Select(i => MakeUncutFullDayShift(today.AddDays(i))).ToArray();
        SetupQuery(shifts);

        await _sut.DetectAsync();

        await _groupScopeReader.Received(1).GetGroupIdsByShiftIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DetectAsync_AucklandCompanyDayAcrossUtcMidnight_UsesCompanyDayNotUtcDay()
    {
        // UTC instant 2026-06-27T23:30Z is still 27.06 in UTC but already 28.06 11:30 in Pacific/Auckland
        // (+12:00, no DST in the southern-hemisphere winter). A duty starting 2026-06-30 is 2 days out
        // under the (correct) company day but 3 days out under the (wrong) UTC day.
        var instant = DateTimeOffset.Parse(
            "2026-06-27T23:30:00Z", System.Globalization.CultureInfo.InvariantCulture,
            System.Globalization.DateTimeStyles.AdjustToUniversal);
        var clock = new FixedCompanyClock(instant, TimeZoneInfo.FindSystemTimeZoneById("Pacific/Auckland"));
        _sut = new UncutFullDayShiftDetector(
            _shiftRepository, _groupScopeReader, _agentConditionRepository, clock, NullLogger<UncutFullDayShiftDetector>.Instance);
        var shift = MakeUncutFullDayShift(new DateOnly(2026, 6, 30));
        SetupQuery(shift);

        var events = await _sut.DetectAsync();

        var e = events.Single() as UncutFullDayShiftTriggerEvent;
        Assert.That(e!.DaysUntil, Is.EqualTo(2),
            "Company day (Pacific/Auckland) is already 28.06 at this UTC instant; the detector must not fall back to the UTC day 27.06.");
    }
}
