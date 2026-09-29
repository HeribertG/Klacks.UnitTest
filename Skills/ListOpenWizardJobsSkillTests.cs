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
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Interfaces.Schedules.AutoWizard;
using Klacks.Api.Application.Interfaces.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
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
    private ListOpenWizardJobsSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _wizard = Substitute.For<IWizardJobRunner>();
        _harmonizer = Substitute.For<IHarmonizerJobRunner>();
        _holistic = Substitute.For<IHolisticHarmonizerJobRunner>();
        _autoWizard = Substitute.For<IAutoWizardJobRunner>();
        _states = JobTerminalStateCacheTestFactory.Create<AutoWizardJobResultDto>();
        _skill = new ListOpenWizardJobsSkill(_wizard, _harmonizer, _holistic, _autoWizard, _states);
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
}
