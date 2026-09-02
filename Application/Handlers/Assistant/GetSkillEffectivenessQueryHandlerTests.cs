// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the "Skill-Wirksamkeit" scorecard handler (W6.1). Pins the three things the aggregation
/// can silently get wrong: the reporting window that every repository call has to share, the minimum
/// call count below which a skill must not be ranked at all, and the flop threshold that keeps the
/// flop table from degenerating into the reversed top table when fewer than twice TopFlopLimit skills
/// qualify. The eval trend is bounded by count instead of by the window, which is asserted separately
/// because it is the one number the window must not influence.
/// </summary>

using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Queries.Assistant;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Handlers.Assistant;

[TestFixture]
public class GetSkillEffectivenessQueryHandlerTests
{
    private const double WindowToleranceMinutes = 1;
    private const string PerfectSkill = "list_clients";
    private const string StrongSkill = "list_shifts";
    private const string GoodSkill = "list_groups";
    private const string BorderlineSkill = "list_absences";
    private const string WeakSkill = "create_shift";
    private const string WorstSkill = "open_client_form";

    private ISkillEffectivenessRepository _repository = null!;
    private GetSkillEffectivenessQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ISkillEffectivenessRepository>();

        _repository.GetEvalTrendAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<EvalRun>)new List<EvalRun>());
        _repository.GetRecipeFunnelAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RecipeFunnelRow>)new List<RecipeFunnelRow>());
        _repository.GetUsageCountAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(0);
        _repository.GetFailureCountsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SkillFailureKindCount>)new List<SkillFailureKindCount>());
        _repository.GetSkillCallStatsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SkillCallStat>)new List<SkillCallStat>());
        _repository.GetChosenSourceSampleAsync(Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<TrajectoryChosenSourceSample>)new List<TrajectoryChosenSourceSample>());

        _handler = new GetSkillEffectivenessQueryHandler(_repository);
    }

    [Test]
    public async Task AQueryWithoutAWindow_ReportsOverTheDefaultWindow()
    {
        var result = await _handler.Handle(new GetSkillEffectivenessQuery(), CancellationToken.None);

        result.Days.ShouldBe(SkillEffectivenessDefaults.DefaultDays);
        ShouldHaveWindowedEveryQueryBy(SkillEffectivenessDefaults.DefaultDays);
    }

    [Test]
    public async Task AnExplicitWindow_ReachesEveryWindowedQuery()
    {
        const int days = 7;

        var result = await _handler.Handle(
            new GetSkillEffectivenessQuery { Days = days }, CancellationToken.None);

        result.Days.ShouldBe(days);
        ShouldHaveWindowedEveryQueryBy(days);
    }

    // The controller answers 400 for an out-of-range window, so the handler never sees one from HTTP.
    // It clamps anyway: a zero or negative window would otherwise produce a lower bound in the future
    // and report an empty scorecard as if the telemetry were empty.
    [Test]
    public async Task AWindowBelowTheMinimum_IsClampedInsteadOfInverted()
    {
        var result = await _handler.Handle(
            new GetSkillEffectivenessQuery { Days = 0 }, CancellationToken.None);

        result.Days.ShouldBe(SkillEffectivenessDefaults.MinDays);
        ShouldHaveWindowedEveryQueryBy(SkillEffectivenessDefaults.MinDays);
    }

    [Test]
    public async Task AWindowAboveTheMaximum_IsClampedToTheMaximum()
    {
        var result = await _handler.Handle(
            new GetSkillEffectivenessQuery { Days = SkillEffectivenessDefaults.MaxDays + 1 },
            CancellationToken.None);

        result.Days.ShouldBe(SkillEffectivenessDefaults.MaxDays);
        ShouldHaveWindowedEveryQueryBy(SkillEffectivenessDefaults.MaxDays);
    }

    [Test]
    public async Task SuccessesAndSuccessRate_AreDerivedFromCallsAndFailures()
    {
        GivenCallStats(new SkillCallStat(PerfectSkill, 20, 5));

        var result = await _handler.Handle(new GetSkillEffectivenessQuery(), CancellationToken.None);

        var stat = result.TopSkills.ShouldHaveSingleItem();
        stat.SkillName.ShouldBe(PerfectSkill);
        stat.Calls.ShouldBe(20);
        stat.Failures.ShouldBe(5);
        stat.Successes.ShouldBe(15);
        stat.SuccessRate.ShouldBe(0.75, 1e-9);
    }

    // A skill with a single successful call would otherwise sit at the top of the table with a perfect
    // rate and no evidence behind it.
    [Test]
    public async Task ASkillBelowTheMinimumCallCount_IsNotRankedAtAll()
    {
        GivenCallStats(
            new SkillCallStat(PerfectSkill, SkillEffectivenessDefaults.TopFlopMinCalls - 1, 0),
            new SkillCallStat(StrongSkill, SkillEffectivenessDefaults.TopFlopMinCalls, 1));

        var result = await _handler.Handle(new GetSkillEffectivenessQuery(), CancellationToken.None);

        result.TopSkills.Select(s => s.SkillName).ShouldBe(new[] { StrongSkill });
        result.FlopSkills.ShouldBeEmpty();
    }

    [Test]
    public async Task ASkillExactlyAtTheMinimumCallCount_IsRanked()
    {
        GivenCallStats(new SkillCallStat(PerfectSkill, SkillEffectivenessDefaults.TopFlopMinCalls, 0));

        var result = await _handler.Handle(new GetSkillEffectivenessQuery(), CancellationToken.None);

        result.TopSkills.ShouldHaveSingleItem().SkillName.ShouldBe(PerfectSkill);
    }

    // The regression the flop threshold exists to prevent: with fewer than twice TopFlopLimit ranked
    // skills, an unfiltered flop table is simply the top table read backwards, so a healthy skill shows
    // up as a problem case. Only skills below FlopMaxSuccessRate may appear.
    [Test]
    public async Task TheFlopTable_IsNotTheReversedTopTable()
    {
        GivenCallStats(
            new SkillCallStat(PerfectSkill, 5, 0),
            new SkillCallStat(StrongSkill, 20, 1),
            new SkillCallStat(GoodSkill, 10, 1),
            new SkillCallStat(BorderlineSkill, 20, 3),
            new SkillCallStat(WeakSkill, 6, 3),
            new SkillCallStat(WorstSkill, 5, 4));

        var result = await _handler.Handle(new GetSkillEffectivenessQuery(), CancellationToken.None);

        result.TopSkills.Select(s => s.SkillName).ShouldBe(new[]
        {
            PerfectSkill, StrongSkill, GoodSkill, BorderlineSkill, WeakSkill, WorstSkill
        });
        result.FlopSkills.Select(s => s.SkillName).ShouldBe(new[] { WorstSkill, WeakSkill });
        result.FlopSkills.ShouldAllBe(s => s.SuccessRate < SkillEffectivenessDefaults.FlopMaxSuccessRate);

        // Guards the fixture itself: the borderline skill is only meaningful while it sits above the
        // threshold, so a moved threshold must fail here instead of quietly weakening the assertion.
        result.TopSkills.Single(s => s.SkillName == BorderlineSkill).SuccessRate
            .ShouldBeGreaterThan(SkillEffectivenessDefaults.FlopMaxSuccessRate);
        result.FlopSkills.ShouldNotContain(s => s.SkillName == BorderlineSkill);
    }

    [Test]
    public async Task TheTopTable_IsBoundedByTheRowLimit()
    {
        var stats = Enumerable.Range(0, SkillEffectivenessDefaults.TopFlopLimit + 3)
            .Select(i => new SkillCallStat($"skill_{i:00}", 20, i))
            .ToArray();
        GivenCallStats(stats);

        var result = await _handler.Handle(new GetSkillEffectivenessQuery(), CancellationToken.None);

        result.TopSkills.Count.ShouldBe(SkillEffectivenessDefaults.TopFlopLimit);
    }

    [Test]
    public async Task TheFailureClasses_AreMappedAndTheHallucinationRateIsNotFoundOverAllRows()
    {
        _repository.GetUsageCountAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(200);
        _repository.GetFailureCountsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SkillFailureKindCount>)new List<SkillFailureKindCount>
            {
                new(SkillFailureKind.NotFound, 50),
                new(SkillFailureKind.PermissionDenied, 4),
                new(SkillFailureKind.ParameterInvalid, 3),
                new(SkillFailureKind.GateHold, 2),
                new(SkillFailureKind.UiActionContext, 1),
                new(SkillFailureKind.Exception, 7)
            });

        var result = await _handler.Handle(new GetSkillEffectivenessQuery(), CancellationToken.None);

        var summary = result.FailureSummary;
        summary.TotalRows.ShouldBe(200);
        summary.NotFound.ShouldBe(50);
        summary.PermissionDenied.ShouldBe(4);
        summary.ParameterInvalid.ShouldBe(3);
        summary.GateHold.ShouldBe(2);
        summary.UiActionContext.ShouldBe(1);
        summary.Exception.ShouldBe(7);
        summary.HallucinationRate.ShouldBe(0.25, 1e-9);
    }

    // An empty window has no rows to divide by. The rate has to be reported as zero rather than as a
    // division by zero, and a stray failure count must not turn into an infinite rate.
    [Test]
    public async Task AnEmptyWindow_ReportsNoHallucinationInsteadOfDividingByZero()
    {
        _repository.GetUsageCountAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(0);
        _repository.GetFailureCountsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SkillFailureKindCount>)new List<SkillFailureKindCount>
            {
                new(SkillFailureKind.NotFound, 3)
            });

        var result = await _handler.Handle(new GetSkillEffectivenessQuery(), CancellationToken.None);

        result.FailureSummary.TotalRows.ShouldBe(0);
        result.FailureSummary.NotFound.ShouldBe(3);
        result.FailureSummary.HallucinationRate.ShouldBe(0);
    }

    // Goldset runs are rare enough that any window would usually empty the trend table, so the trend is
    // bounded by count. The repository signature makes a window impossible; what is asserted here is
    // that the requested period does not change the trend query either.
    [Test]
    public async Task TheEvalTrend_IsBoundedByCountAndUnaffectedByTheWindow()
    {
        await _handler.Handle(
            new GetSkillEffectivenessQuery { Days = SkillEffectivenessDefaults.MinDays }, CancellationToken.None);
        await _handler.Handle(
            new GetSkillEffectivenessQuery { Days = SkillEffectivenessDefaults.MaxDays }, CancellationToken.None);

        await _repository.Received(2).GetEvalTrendAsync(
            SkillEffectivenessDefaults.EvalTrendLimit, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheEvalTrend_IsMappedRunByRun()
    {
        var created = new DateTime(2026, 8, 30, 4, 30, 0, DateTimeKind.Utc);
        _repository.GetEvalTrendAsync(Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<EvalRun>)new List<EvalRun>
            {
                new()
                {
                    Goldset = "skills-de",
                    Model = "claude",
                    CompositeScore = 0.82m,
                    ItemsTotal = 40,
                    ItemsPassed = 33,
                    CreateTime = created
                }
            });

        var result = await _handler.Handle(new GetSkillEffectivenessQuery(), CancellationToken.None);

        var run = result.EvalTrend.ShouldHaveSingleItem();
        run.Goldset.ShouldBe("skills-de");
        run.Model.ShouldBe("claude");
        run.CompositeScore.ShouldBe(0.82m);
        run.ItemsTotal.ShouldBe(40);
        run.ItemsPassed.ShouldBe(33);
        run.CreateTime.ShouldBe(created);
    }

    [Test]
    public async Task TheRecipeFunnel_IsMappedRowByRow()
    {
        _repository.GetRecipeFunnelAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RecipeFunnelRow>)new List<RecipeFunnelRow>
            {
                new("absence_request", 11, 2, 6, 2, 1)
            });

        var result = await _handler.Handle(new GetSkillEffectivenessQuery(), CancellationToken.None);

        var row = result.RecipeFunnel.ShouldHaveSingleItem();
        row.RecipeName.ShouldBe("absence_request");
        row.Started.ShouldBe(11);
        row.Running.ShouldBe(2);
        row.Completed.ShouldBe(6);
        row.Aborted.ShouldBe(2);
        row.Expired.ShouldBe(1);
    }

    [Test]
    public async Task TheTrajectorySample_IsBoundedByTheSampleLimitAndDistributedBySource()
    {
        const string candidates = """[{"name":"list_clients","source":"Retrieved"}]""";
        _repository.GetChosenSourceSampleAsync(
                Arg.Any<DateTime>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<TrajectoryChosenSourceSample>)new List<TrajectoryChosenSourceSample>
            {
                new(PerfectSkill, candidates),
                new(PerfectSkill, candidates),
                new(null, candidates)
            });

        var result = await _handler.Handle(new GetSkillEffectivenessQuery(), CancellationToken.None);

        await _repository.Received(1).GetChosenSourceSampleAsync(
            Arg.Any<DateTime>(),
            SkillEffectivenessDefaults.TrajectorySampleLimit,
            Arg.Any<CancellationToken>());
        result.ChosenSourceDistribution.First().Source.ShouldBe("Retrieved");
        result.ChosenSourceDistribution.First().Count.ShouldBe(2);
    }

    private void GivenCallStats(params SkillCallStat[] stats)
    {
        _repository.GetSkillCallStatsAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SkillCallStat>)stats.ToList());
    }

    // Every windowed repository method takes the lower bound as its first argument. Reading the bounds
    // back off the received calls instead of matching them with an argument matcher keeps a mismatch
    // readable: a failure reports the actual date, not "no matching call".
    private void ShouldHaveWindowedEveryQueryBy(int days)
    {
        var expected = DateTime.UtcNow.AddDays(-days);
        var bounds = _repository.ReceivedCalls()
            .Select(call => call.GetArguments().FirstOrDefault())
            .OfType<DateTime>()
            .ToList();

        bounds.Count.ShouldBe(5, "recipe funnel, usage count, failure counts, call stats and the "
            + "trajectory sample all report over the same window.");
        foreach (var bound in bounds)
        {
            bound.Kind.ShouldBe(DateTimeKind.Utc);
            Math.Abs((bound - expected).TotalMinutes).ShouldBeLessThan(WindowToleranceMinutes);
        }

        bounds.Distinct().Count().ShouldBe(1, "a scorecard whose panels cover different periods "
            + "cannot be compared panel to panel.");
    }
}
