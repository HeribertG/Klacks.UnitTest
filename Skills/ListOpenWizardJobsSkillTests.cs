// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the job-status skill Klacksy uses to follow up on an automatic planning run. Before the
/// fix it only asked the live registries, so every finished job answered "not in any registry" and a failed
/// run was never reported. It now reads the stored terminal state: completed (with or without the holistic
/// harmonization), failed with its reason, and the scenario the user can accept.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Application.DTOs.Schedules.KeywordWishes;
using Klacks.Api.Application.DTOs.Schedules.Summary;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Interfaces.Schedules.AutoWizard;
using Klacks.Api.Application.Interfaces.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.ScheduleOptimizer.Models;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListOpenWizardJobsSkillTests
{
    private IWizardJobRunner _wizard = null!;
    private IHarmonizerJobRunner _harmonizer = null!;
    private IHolisticHarmonizerJobRunner _holistic = null!;
    private IAutoWizardJobRunner _autoWizard = null!;
    private JobTerminalStateCache<AutoWizardJobResultDto> _states = null!;
    private IAutoWizardJobAccessGuard _jobAccess = null!;
    private ListOpenWizardJobsSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _wizard = Substitute.For<IWizardJobRunner>();
        _harmonizer = Substitute.For<IHarmonizerJobRunner>();
        _holistic = Substitute.For<IHolisticHarmonizerJobRunner>();
        _autoWizard = Substitute.For<IAutoWizardJobRunner>();
        _states = JobTerminalStateCacheTestFactory.Create<AutoWizardJobResultDto>();
        _jobAccess = Substitute.For<IAutoWizardJobAccessGuard>();
        _jobAccess.CanAccessAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _skill = new ListOpenWizardJobsSkill(_wizard, _harmonizer, _holistic, _autoWizard, _states, _jobAccess);
    }

    [Test]
    public async Task RunningDeveloperDropdownJob_IsNotReportedToAPlanner()
    {
        var jobId = Guid.NewGuid();
        _wizard.IsRunning(jobId).Returns(true);
        var planner = new SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "planner",
            UserPermissions = new List<string> { "CanEditSchedule" }
        };

        var result = await _skill.ExecuteAsync(planner, JobParameter(jobId));

        DataAsJson(result).GetProperty("Status").GetString().ShouldBe(WizardJobStatusValues.Unknown);
    }

    [Test]
    public async Task JobTheCallerMayNotAddress_IsAnsweredLikeAnUnknownJob()
    {
        var jobId = Guid.NewGuid();
        _autoWizard.IsRunning(jobId).Returns(true);
        await _states.StoreCompletedAsync(jobId, Result(jobId, Guid.NewGuid(), skipped: false, skipReason: null));
        _jobAccess.CanAccessAsync(jobId, Arg.Any<CancellationToken>()).Returns(false);

        var result = await _skill.ExecuteAsync(Ctx(), JobParameter(jobId));

        var data = DataAsJson(result);
        data.GetProperty("Status").GetString().ShouldBe(WizardJobStatusValues.Unknown);
        data.TryGetProperty("ScenarioId", out _).ShouldBeFalse();
        result.Message.ShouldNotContain("still running");
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "Admin" }
    };

    private static Dictionary<string, object> JobParameter(Guid jobId) => new() { ["jobId"] = jobId.ToString() };

    private static JsonElement DataAsJson(SkillResult result) => JsonSerializer.SerializeToElement(result.Data);

    private static AutoWizardJobResultDto Result(Guid jobId, Guid scenarioId, bool skipped, string? skipReason) =>
        new(jobId, scenarioId, Guid.NewGuid(), "Auto Winterthur 02.11.26", 1000, [], [], [],
            HarmonizationSkipped: skipped, HarmonizationSkippedReason: skipReason);

    [Test]
    public async Task RunningJob_ReportsRunning()
    {
        var jobId = Guid.NewGuid();
        _autoWizard.IsRunning(jobId).Returns(true);

        var result = await _skill.ExecuteAsync(Ctx(), JobParameter(jobId));

        DataAsJson(result).GetProperty("Status").GetString().ShouldBe(WizardJobStatusValues.Running);
        result.Message.ShouldContain("still running");
    }

    [Test]
    public async Task CompletedJob_ReportsTheScenarioToAccept()
    {
        var jobId = Guid.NewGuid();
        var scenarioId = Guid.NewGuid();
        await _states.StoreCompletedAsync(jobId, Result(jobId, scenarioId, skipped: false, skipReason: null));

        var result = await _skill.ExecuteAsync(Ctx(), JobParameter(jobId));

        var data = DataAsJson(result);
        data.GetProperty("Status").GetString().ShouldBe(WizardJobStatusValues.Completed);
        data.GetProperty("ScenarioId").GetGuid().ShouldBe(scenarioId);
        data.GetProperty("HarmonizationSkipped").GetBoolean().ShouldBeFalse();
        result.Message.ShouldContain("completed all stages");
        result.Message.ShouldContain(scenarioId.ToString());
    }

    [Test]
    public async Task CompletedWithoutHarmonization_SaysSoHonestly()
    {
        var jobId = Guid.NewGuid();
        await _states.StoreCompletedAsync(jobId, Result(jobId, Guid.NewGuid(), skipped: true, skipReason: "no vision model"));

        var result = await _skill.ExecuteAsync(Ctx(), JobParameter(jobId));

        var data = DataAsJson(result);
        data.GetProperty("HarmonizationSkipped").GetBoolean().ShouldBeTrue();
        data.GetProperty("HarmonizationSkippedReason").GetString().ShouldBe("no vision model");
        result.Message.ShouldContain("WITHOUT the holistic harmonization");
        result.Message.ShouldContain("no vision model");
    }

    [Test]
    public async Task FailedJob_ReportsReasonAndPartialScenario()
    {
        var jobId = Guid.NewGuid();
        var partialId = Guid.NewGuid();
        await _states.StoreFailedAsync(
            jobId, "AutoWizard failed in the HolisticHarmonizer stage: engine crashed",
            Result(jobId, partialId, skipped: false, skipReason: null));

        var result = await _skill.ExecuteAsync(Ctx(), JobParameter(jobId));

        var data = DataAsJson(result);
        data.GetProperty("Status").GetString().ShouldBe(WizardJobStatusValues.Failed);
        data.GetProperty("Reason").GetString()!.ShouldContain("engine crashed");
        data.GetProperty("ScenarioId").GetGuid().ShouldBe(partialId);
        result.Message.ShouldContain("FAILED");
    }

    [Test]
    public async Task UnknownJob_PointsToTheOpenScenarios()
    {
        var jobId = Guid.NewGuid();

        var result = await _skill.ExecuteAsync(Ctx(), JobParameter(jobId));

        DataAsJson(result).GetProperty("Status").GetString().ShouldBe(WizardJobStatusValues.Unknown);
        result.Message.ShouldContain("open scenarios");
    }

    [Test]
    public async Task InvalidJobId_ReturnsError()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["jobId"] = "nope" });

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task CompletedJobWithSoftAlternative_NamesTheExtraSlotsAndTheBrokenWishes()
    {
        var jobId = Guid.NewGuid();
        var softId = Guid.NewGuid();
        var clientId = Guid.NewGuid();
        var day = new DateOnly(2026, 11, 3);
        var strictSummary = new ScenarioSummaryDto(Guid.NewGuid(), day, day, 1, 8, 10, 8, [], []);
        var softSummary = new ScenarioSummaryDto(Guid.NewGuid(), day, day, 1, 10, 10, 10, [], []);
        await _states.StoreCompletedAsync(jobId, Result(jobId, Guid.NewGuid(), skipped: false, skipReason: null) with
        {
            Summary = strictSummary,
            SoftKeywordRetryOutcome = AutoWizardSoftKeywordRetryOutcomes.Kept,
            SoftAlternative = new AutoWizardSoftAlternativeDto(
                softId, Guid.NewGuid(), "Auto soft", KeywordMode.Soft, 2, softSummary,
                [new AutoWizardBrokenWishDto(clientId, "Anna Muster", day, "Free", "BrokenFree")])
        });

        var result = await _skill.ExecuteAsync(Ctx(), JobParameter(jobId));

        result.Message.ShouldContain("leaves 2 slot(s) open");
        result.Message.ShouldContain($"'Auto soft' (id {softId})");
        result.Message.ShouldContain("fills 2 more slot(s)");
        result.Message.ShouldContain("Anna Muster on 2026-11-03 (Free, BrokenFree)");
        DataAsJson(result).GetProperty("SoftAlternative").GetProperty("ScenarioId").GetGuid().ShouldBe(softId);
    }

    [Test]
    public async Task CompletedJobWhoseSoftRetryWasNoBetter_SaysSo()
    {
        var jobId = Guid.NewGuid();
        await _states.StoreCompletedAsync(jobId, Result(jobId, Guid.NewGuid(), skipped: false, skipReason: null) with
        {
            SoftKeywordRetryOutcome = AutoWizardSoftKeywordRetryOutcomes.NotBetter
        });

        var result = await _skill.ExecuteAsync(Ctx(), JobParameter(jobId));

        result.Message.ShouldContain("covered no more slots");
        DataAsJson(result).GetProperty("SoftAlternative").ValueKind.ShouldBe(JsonValueKind.Null);
    }

    [Test]
    public async Task CompletedJobWithSoftAlternative_ReportsBrokenRestrictionsAndUnfulfilledWishesApart()
    {
        var jobId = Guid.NewGuid();
        var day = new DateOnly(2026, 11, 3);
        var strictSummary = new ScenarioSummaryDto(Guid.NewGuid(), day, day, 1, 8, 10, 8, [], []);
        var softSummary = new ScenarioSummaryDto(Guid.NewGuid(), day, day, 1, 10, 10, 10, [], [],
            KeywordWishes: new KeywordWishesDto(1, 0, 0, 2, []));
        await _states.StoreCompletedAsync(jobId, Result(jobId, Guid.NewGuid(), skipped: false, skipReason: null) with
        {
            Summary = strictSummary,
            SoftKeywordRetryOutcome = AutoWizardSoftKeywordRetryOutcomes.Kept,
            SoftAlternative = new AutoWizardSoftAlternativeDto(
                Guid.NewGuid(), Guid.NewGuid(), "Auto soft", KeywordMode.Soft, 2, softSummary, [])
        });

        var result = await _skill.ExecuteAsync(Ctx(), JobParameter(jobId));

        result.Message.ShouldContain("breaks 1 restricting wish(es)");
        result.Message.ShouldContain("leaves 2 positive wish(es) unfulfilled");
        var soft = DataAsJson(result).GetProperty("SoftAlternative");
        soft.GetProperty("BrokenRestrictions").GetInt32().ShouldBe(1);
        soft.GetProperty("UnfulfilledPositiveWishes").GetInt32().ShouldBe(2);
    }

    [Test]
    public async Task CompletedJobWithPromotedSoftScenario_SaysItReplacedTheFirstPlanWithoutBreakingAWish()
    {
        var jobId = Guid.NewGuid();
        var day = new DateOnly(2026, 11, 3);
        await _states.StoreCompletedAsync(jobId, Result(jobId, Guid.NewGuid(), skipped: false, skipReason: null) with
        {
            Summary = new ScenarioSummaryDto(Guid.NewGuid(), day, day, 1, 10, 10, 10, [], [],
                KeywordWishes: new KeywordWishesDto(0, 0, 0, 2, [])),
            SoftKeywordRetryOutcome = AutoWizardSoftKeywordRetryOutcomes.Promoted
        });

        var result = await _skill.ExecuteAsync(Ctx(), JobParameter(jobId));

        result.Message.ShouldContain("found a better plan without breaking any wish");
        result.Message.ShouldContain("leaves 2 positive wish(es) unfulfilled");
        DataAsJson(result).GetProperty("SoftAlternative").ValueKind.ShouldBe(JsonValueKind.Null);
    }
}
