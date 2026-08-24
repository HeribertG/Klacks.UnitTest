// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for UncutFullDayShiftDetector -- covers empty-result, severity-by-proximity
/// (High/Medium/Low, including an already-started duty), the SplitShift/unequal-times/already-
/// ended/container-shift negative cases, scenario-clone exclusion, soft-delete exclusion,
/// DedupKey stability, and that a large backlog of long-past duties cannot crowd a genuinely
/// upcoming one out of the per-tick emission cap. The container-shift exclusion pins the design
/// decision that "cut" is a task-level concept: a container with equal StartShift/EndShift is
/// exclusively EmptyContainerDetector's concern, never this detector's.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class UncutFullDayShiftDetectorTests
{
    private IShiftRepository _shiftRepository = null!;
    private UncutFullDayShiftDetector _sut = null!;

    [SetUp]
    public void Setup()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _sut = new UncutFullDayShiftDetector(_shiftRepository, NullLogger<UncutFullDayShiftDetector>.Instance);
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
}
