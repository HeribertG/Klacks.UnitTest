// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// cancel_wizard_job is open to planners since autofill is planning work (owner rule 2026-11-07). It tries every wizard
/// family, so the job access rule decides first: a job the caller may not address (a hidden group's AutoWizard job, or a
/// developer-dropdown job for a restricted caller) is answered like an unknown job and nothing is cancelled. The
/// single-stage wizards of the developer dropdown are cancelled for admins only, even in an installation without groups
/// where a planner counts as unrestricted.
/// </summary>

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Interfaces.Schedules.AutoWizard;
using Klacks.Api.Application.Interfaces.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CancelWizardJobSkillTests
{
    private IWizardJobRunner _wizard = null!;
    private IHarmonizerJobRunner _harmonizer = null!;
    private IHolisticHarmonizerJobRunner _holistic = null!;
    private IAutoWizardJobRunner _autoWizard = null!;
    private IAutoWizardJobAccessGuard _access = null!;
    private CancelWizardJobSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _wizard = Substitute.For<IWizardJobRunner>();
        _harmonizer = Substitute.For<IHarmonizerJobRunner>();
        _holistic = Substitute.For<IHolisticHarmonizerJobRunner>();
        _autoWizard = Substitute.For<IAutoWizardJobRunner>();
        _access = Substitute.For<IAutoWizardJobAccessGuard>();
        _skill = new CancelWizardJobSkill(_wizard, _harmonizer, _holistic, _autoWizard, _access);
    }

    private static SkillExecutionContext Ctx(params string[] rights) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "planner",
        UserPermissions = rights.Length == 0 ? new List<string> { Permissions.CanEditSchedule } : rights.ToList()
    };

    private static Dictionary<string, object> JobParameter(Guid jobId) => new() { ["jobId"] = jobId.ToString() };

    [Test]
    public async Task AddressableJob_IsCancelled()
    {
        var jobId = Guid.NewGuid();
        _access.CanAccessAsync(jobId, Arg.Any<CancellationToken>()).Returns(true);
        _autoWizard.TryCancel(jobId).Returns(true);

        var result = await _skill.ExecuteAsync(Ctx(), JobParameter(jobId));

        result.Success.ShouldBeTrue();
        _autoWizard.Received(1).TryCancel(jobId);
    }

    [Test]
    public async Task DeveloperDropdownJob_IsNotCancelledForAPlanner()
    {
        var jobId = Guid.NewGuid();
        _access.CanAccessAsync(jobId, Arg.Any<CancellationToken>()).Returns(true);
        _wizard.TryCancel(jobId).Returns(true);

        var result = await _skill.ExecuteAsync(Ctx(), JobParameter(jobId));

        result.Success.ShouldBeFalse();
        _wizard.DidNotReceive().TryCancel(Arg.Any<Guid>());
    }

    [Test]
    public async Task DeveloperDropdownJob_IsCancelledForAnAdmin()
    {
        var jobId = Guid.NewGuid();
        _access.CanAccessAsync(jobId, Arg.Any<CancellationToken>()).Returns(true);
        _wizard.TryCancel(jobId).Returns(true);

        var result = await _skill.ExecuteAsync(Ctx(Roles.Admin), JobParameter(jobId));

        result.Success.ShouldBeTrue();
        _wizard.Received(1).TryCancel(jobId);
    }

    [Test]
    public async Task JobTheCallerMayNotAddress_IsAnsweredLikeAnUnknownJobAndNotCancelled()
    {
        var jobId = Guid.NewGuid();
        _access.CanAccessAsync(jobId, Arg.Any<CancellationToken>()).Returns(false);
        _autoWizard.TryCancel(jobId).Returns(true);
        _wizard.TryCancel(jobId).Returns(true);

        var result = await _skill.ExecuteAsync(Ctx(), JobParameter(jobId));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("unknown or already finished");
        _autoWizard.DidNotReceive().TryCancel(Arg.Any<Guid>());
        _wizard.DidNotReceive().TryCancel(Arg.Any<Guid>());
        _harmonizer.DidNotReceive().TryCancel(Arg.Any<Guid>());
        _holistic.DidNotReceive().TryCancel(Arg.Any<Guid>());
    }
}
