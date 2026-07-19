// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the EvaluateScenarioQuery handler: scenario resolution (by token / by id /
/// not found), rule-compliance projection from the period validator, and the clone-safe,
/// producer-agnostic change-set diff of the effective grid. The cover_absence case pins the
/// critical property that a cloned-real work does NOT show as a change while the new replacement
/// and absence DO — i.e. the diff is not blind to WorkChange/Break producers.
/// </summary>

using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.PeriodClosing;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.Schedules;

[TestFixture]
public class EvaluateScenarioQueryHandlerTests
{
    private static readonly Guid Token = Guid.NewGuid();
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid Anna = Guid.NewGuid();
    private static readonly Guid Cara = Guid.NewGuid();
    private static readonly DateOnly From = new(2026, 3, 2);
    private static readonly DateOnly Until = new(2026, 3, 8);
    private static readonly DateTime Day = new(2026, 3, 3);

    private IAnalyseScenarioRepository _scenarioRepo = null!;
    private IScheduleEntriesService _entries = null!;
    private IPeriodValidationLoader _loader = null!;

    [SetUp]
    public void Setup()
    {
        _scenarioRepo = Substitute.For<IAnalyseScenarioRepository>();
        _entries = Substitute.For<IScheduleEntriesService>();
        _loader = Substitute.For<IPeriodValidationLoader>();

        _loader.LoadAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<PeriodIssueDto>());
    }

    private EvaluateScenarioQueryHandler Handler() => new(_scenarioRepo, _entries, _loader);

    private static AnalyseScenario Scenario() => new()
    {
        Id = Guid.NewGuid(),
        Token = Token,
        Name = "Proposal",
        Status = AnalyseScenarioStatus.Active,
        GroupId = GroupId,
        FromDate = From,
        UntilDate = Until
    };

    private static ScheduleCell Cell(
        int entryType, Guid clientId, TimeSpan start, TimeSpan end,
        string? shift = "DayShift", Guid? replaceClientId = null, bool isReplacement = false) => new()
    {
        Id = Guid.NewGuid(),
        SourceId = Guid.NewGuid(),
        EntryId = Guid.NewGuid(),
        EntryType = entryType,
        ClientId = clientId,
        EntryDate = Day,
        StartTime = start,
        EndTime = end,
        EntryName = shift,
        ReplaceClientId = replaceClientId,
        IsReplacementEntry = isReplacement
    };

    private void SetGrid(IEnumerable<ScheduleCell> real, IEnumerable<ScheduleCell> scenario)
    {
        _entries.GetScheduleEntriesQuery(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<List<Guid>?>(),
                Arg.Is<Guid?>(t => !t.HasValue))
            .Returns(new TestAsyncEnumerable<ScheduleCell>(real));
        _entries.GetScheduleEntriesQuery(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<List<Guid>?>(),
                Arg.Is<Guid?>(t => t == Token))
            .Returns(new TestAsyncEnumerable<ScheduleCell>(scenario));
    }

    [Test]
    public async Task NotFound_WhenScenarioMissing()
    {
        _scenarioRepo.GetByTokenAsync(Token, Arg.Any<CancellationToken>()).Returns((AnalyseScenario?)null);

        var result = await Handler().Handle(new EvaluateScenarioQuery(null, Token), CancellationToken.None);

        result.Found.ShouldBeFalse();
    }

    [Test]
    public async Task ResolvesById_WhenTokenOmitted()
    {
        var scenario = Scenario();
        _scenarioRepo.Get(scenario.Id).Returns(scenario);
        SetGrid([], []);

        var result = await Handler().Handle(new EvaluateScenarioQuery(scenario.Id, null), CancellationToken.None);

        result.Found.ShouldBeTrue();
        await _scenarioRepo.Received(1).Get(scenario.Id);
    }

    [Test]
    public async Task ProposePlan_AddedWork_IsDetected_AndRuleClean()
    {
        _scenarioRepo.GetByTokenAsync(Token, Arg.Any<CancellationToken>()).Returns(Scenario());
        var addedWork = Cell((int)ScheduleEntryType.Work, Cara, new TimeSpan(8, 0, 0), new TimeSpan(16, 0, 0));
        SetGrid(real: [], scenario: [addedWork]);

        var result = await Handler().Handle(new EvaluateScenarioQuery(null, Token), CancellationToken.None);

        result.Found.ShouldBeTrue();
        result.RuleCompliant.ShouldBeTrue();
        result.AddedEntryCount.ShouldBe(1);
        result.AddedWorkEntries.ShouldBe(1);
        result.RemovedEntryCount.ShouldBe(0);
        result.Recommendation.ShouldContain("rule-clean");
    }

    [Test]
    public async Task CoverAbsence_ClonedWorkIsNotAChange_ReplacementAndBreakAre()
    {
        _scenarioRepo.GetByTokenAsync(Token, Arg.Any<CancellationToken>()).Returns(Scenario());

        var work = new TimeSpan(8, 0, 0);
        var workEnd = new TimeSpan(16, 0, 0);

        var realAnnaWork = Cell((int)ScheduleEntryType.Work, Anna, work, workEnd);
        // Scenario after cover_absence: the cloned (byte-identical) Anna work stays, plus the rows the
        // get_schedule_entries SP emits for ONE ReplacementWithin (verified against the SP SQL):
        //  - original side: WorkChange on Anna, is_replacement_entry = false (deducts her time)
        //  - replacement side: WorkChange on Cara, is_replacement_entry = true (gains the slot)
        // plus the absence Break for Anna. The original work is NOT suppressed by the SP.
        var clonedAnnaWork = Cell((int)ScheduleEntryType.Work, Anna, work, workEnd);
        var replacementOriginalSide = Cell((int)ScheduleEntryType.WorkChange, Anna, work, workEnd,
            replaceClientId: Cara, isReplacement: false);
        var replacementSide = Cell((int)ScheduleEntryType.WorkChange, Cara, work, workEnd,
            replaceClientId: Anna, isReplacement: true);
        var absence = Cell((int)ScheduleEntryType.Break, Anna, work, workEnd, shift: null);

        SetGrid(real: [realAnnaWork], scenario: [clonedAnnaWork, replacementOriginalSide, replacementSide, absence]);

        var result = await Handler().Handle(new EvaluateScenarioQuery(null, Token), CancellationToken.None);

        result.AddedEntryCount.ShouldBe(3);
        result.AddedWorkEntries.ShouldBe(0);
        result.AddedReplacementEntries.ShouldBe(1);
        result.AddedBreakEntries.ShouldBe(1);
        result.AddedByType["WorkChange"].ShouldBe(2);
        result.RemovedEntryCount.ShouldBe(0);
    }

    [Test]
    public async Task Errors_BlockRecommendation_AndSetCounts()
    {
        _scenarioRepo.GetByTokenAsync(Token, Arg.Any<CancellationToken>()).Returns(Scenario());
        SetGrid([], []);
        _loader.LoadAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(new List<PeriodIssueDto>
            {
                new()
                {
                    Date = new DateOnly(2026, 3, 3),
                    ClientId = Cara,
                    ClientName = "Cara",
                    Severity = ScheduleValidationType.Error,
                    Code = "collision",
                    MessageKey = "schedule.error-list.collision"
                }
            });

        var result = await Handler().Handle(new EvaluateScenarioQuery(null, Token), CancellationToken.None);

        result.Errors.ShouldBe(1);
        result.RuleCompliant.ShouldBeFalse();
        result.ByCode["collision"].ShouldBe(1);
        result.Recommendation.ShouldContain("do not accept");
    }

    [Test]
    public async Task PassesScenarioTokenToValidator()
    {
        _scenarioRepo.GetByTokenAsync(Token, Arg.Any<CancellationToken>()).Returns(Scenario());
        SetGrid([], []);

        await Handler().Handle(new EvaluateScenarioQuery(null, Token), CancellationToken.None);

        await _loader.Received(1).LoadAsync(From, Until, GroupId, Token, Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }
}
