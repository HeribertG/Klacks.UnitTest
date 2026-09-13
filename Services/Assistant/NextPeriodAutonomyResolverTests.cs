// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for NextPeriodAutonomyResolver - the single decision the next-period detector and the
/// auto-commit watcher share. Two groups of tests: the MIN-over-admins rule with its global cap and the
/// deciding admin the audit records, and the gate matrix that folds the four brakes (kill switch, global
/// level, governance Enabled, governance MaxAction) into CanStartAutofill/CanCommit. The MIN aggregation
/// runs through the REAL AdminAutonomyLevelAggregator rather than a substitute: it is the piece shared
/// with the goal-plan execution path, and stubbing it would stop testing the rule these tests are named
/// after.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class NextPeriodAutonomyResolverTests
{
    private const string FirstAdminId = "3f1c9a52-0000-0000-0000-000000000001";
    private const string SecondAdminId = "3f1c9a52-0000-0000-0000-000000000002";
    private const string ThirdAdminId = "3f1c9a52-0000-0000-0000-000000000003";

    private IPlanningAudienceResolver _audienceResolver = null!;
    private IAgentAutonomyPreferenceRepository _autonomyPreferences = null!;
    private IProactiveGovernanceResolver _governanceResolver = null!;
    private NextPeriodAutonomyResolver _sut = null!;

    [SetUp]
    public void Setup()
    {
        _audienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _autonomyPreferences = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _governanceResolver = Substitute.For<IProactiveGovernanceResolver>();
        _governanceResolver.GetGlobalAutonomyLevelAsync(Arg.Any<CancellationToken>())
            .Returns(AutonomyLevel.FullyAutonomous);
        StubGovernance(ProactiveMaxAction.Execute, enabled: true, killSwitchActive: false);

        _sut = new NextPeriodAutonomyResolver(
            new AdminAutonomyLevelAggregator(_audienceResolver, _autonomyPreferences),
            _governanceResolver);
    }

    /// <summary>
    /// Builds the governance decision the way ProactiveGovernanceResolver would: the kill switch and a
    /// disabled kind pin EffectiveMaxAction to Hint, so a stub that left it at Execute would be a state
    /// production can never reach.
    /// </summary>
    private void StubGovernance(ProactiveMaxAction configuredMaxAction, bool enabled, bool killSwitchActive)
    {
        var effective = killSwitchActive || !enabled ? ProactiveMaxAction.Hint : configuredMaxAction;
        _governanceResolver.ResolveAsync(
                AgentTriggerKinds.NextPeriodSchedulingDue, null, Arg.Any<CancellationToken>())
            .Returns(new ProactiveGovernanceDecision(
                TriggerKind: AgentTriggerKinds.NextPeriodSchedulingDue,
                GroupId: null,
                EffectiveMaxAction: effective,
                ConfiguredMaxAction: configuredMaxAction,
                Enabled: enabled,
                KillSwitchActive: killSwitchActive,
                ResponsibleOwnerUserId: null,
                DailyActionBudget: ProactiveGovernanceDefaults.DailyActionBudget,
                WindowActionLimit: ProactiveGovernanceDefaults.WindowActionLimit,
                WindowMinutes: ProactiveGovernanceDefaults.WindowMinutes,
                IsStored: true,
                GlobalAutonomyCap: ProactiveMaxAction.Execute));
    }

    private void StubAdmins(params (string UserId, AutonomyLevel? Level)[] admins)
    {
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns(admins.Select(admin => admin.UserId).ToHashSet());
        foreach (var (userId, level) in admins)
        {
            _autonomyPreferences.GetAsync(userId, Arg.Any<CancellationToken>())
                .Returns(level is AutonomyLevel stored
                    ? new AgentAutonomyPreferenceRow { UserId = userId, Level = stored }
                    : null);
        }
    }

    [Test]
    public async Task ResolveAsync_NoAdmins_DegradesToProposeWithoutADecidingAdmin()
    {
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>()).Returns(new HashSet<string>());

        var decision = await _sut.ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(decision.EffectiveLevel, Is.EqualTo(AutonomyLevel.Propose));
            Assert.That(decision.DecidingAdminUserId, Is.Null);
            Assert.That(decision.CanStartAutofill, Is.False);
            Assert.That(decision.CanCommit, Is.False);
            Assert.That(decision.BlockedBy, Is.EqualTo(NextPeriodAutonomyBlockedBy.AutonomyLevel));
        });
    }

    [Test]
    public async Task ResolveAsync_OneCautiousAdmin_ThrottlesEverybodyAndIsNamedAsTheDecider()
    {
        StubAdmins(
            (FirstAdminId, AutonomyLevel.FullyAutonomous),
            (SecondAdminId, AutonomyLevel.Assisted),
            (ThirdAdminId, AutonomyLevel.Autonomous));

        var decision = await _sut.ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(decision.EffectiveLevel, Is.EqualTo(AutonomyLevel.Assisted));
            Assert.That(decision.DecidingAdminUserId, Is.EqualTo(Guid.Parse(SecondAdminId)));
        });
    }

    [Test]
    public async Task ResolveAsync_AllAdminsAtTheSameLevel_NamesTheOrdinallyFirstAdmin()
    {
        StubAdmins(
            (ThirdAdminId, AutonomyLevel.FullyAutonomous),
            (SecondAdminId, AutonomyLevel.FullyAutonomous),
            (FirstAdminId, AutonomyLevel.FullyAutonomous));

        var decision = await _sut.ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(decision.EffectiveLevel, Is.EqualTo(AutonomyLevel.FullyAutonomous));
            Assert.That(decision.DecidingAdminUserId, Is.EqualTo(Guid.Parse(FirstAdminId)),
                "The admin set is unordered; without a stable tie-break the audit would name a different admin per run.");
        });
    }

    [Test]
    public async Task ResolveAsync_AdminWithoutAStoredRow_FallsBackToTheDefaultLevel()
    {
        StubAdmins((FirstAdminId, null));

        var decision = await _sut.ResolveAsync();

        Assert.That(decision.EffectiveLevel, Is.EqualTo(AutonomyDefaults.DefaultLevel));
    }

    [Test]
    public async Task ResolveAsync_GlobalCapBelowTheAdminMinimum_WinsAndNamesNobody()
    {
        StubAdmins((FirstAdminId, AutonomyLevel.FullyAutonomous));
        _governanceResolver.GetGlobalAutonomyLevelAsync(Arg.Any<CancellationToken>())
            .Returns(AutonomyLevel.Propose);

        var decision = await _sut.ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(decision.EffectiveLevel, Is.EqualTo(AutonomyLevel.Propose));
            Assert.That(decision.DecidingAdminUserId, Is.Null,
                "The installation setting throttled the run, not an admin - crediting one would be wrong.");
        });
    }

    [Test]
    public async Task ResolveAsync_GlobalCapAboveTheAdminMinimum_LeavesTheAdminDecisionInPlace()
    {
        StubAdmins((FirstAdminId, AutonomyLevel.Autonomous));
        _governanceResolver.GetGlobalAutonomyLevelAsync(Arg.Any<CancellationToken>())
            .Returns(AutonomyLevel.FullyAutonomous);

        var decision = await _sut.ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(decision.EffectiveLevel, Is.EqualTo(AutonomyLevel.Autonomous));
            Assert.That(decision.DecidingAdminUserId, Is.EqualTo(Guid.Parse(FirstAdminId)));
        });
    }

    [Test]
    public async Task ResolveAsync_AdminIdThatIsNotAGuid_ReportsNoDeciderRatherThanEmptyGuid()
    {
        StubAdmins(("not-a-guid", AutonomyLevel.Assisted));

        var decision = await _sut.ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(decision.EffectiveLevel, Is.EqualTo(AutonomyLevel.Assisted));
            Assert.That(decision.DecidingAdminUserId, Is.Null);
        });
    }

    [Test]
    public async Task ResolveAsync_KillSwitchActive_ClosesBothGatesAndNamesTheKillSwitch()
    {
        StubAdmins((FirstAdminId, AutonomyLevel.FullyAutonomous));
        StubGovernance(ProactiveMaxAction.Execute, enabled: true, killSwitchActive: true);

        var decision = await _sut.ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(decision.CanStartAutofill, Is.False);
            Assert.That(decision.CanCommit, Is.False);
            Assert.That(decision.BlockedBy, Is.EqualTo(NextPeriodAutonomyBlockedBy.KillSwitch));
            Assert.That(decision.EffectiveLevel, Is.EqualTo(AutonomyLevel.FullyAutonomous),
                "The level is what the admins chose; the kill switch closes the gates, it does not rewrite consent.");
        });
    }

    [Test]
    public async Task ResolveAsync_KindDisabled_ClosesBothGatesEvenAtFullAutonomy()
    {
        StubAdmins((FirstAdminId, AutonomyLevel.FullyAutonomous));
        StubGovernance(ProactiveMaxAction.Execute, enabled: false, killSwitchActive: false);

        var decision = await _sut.ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(decision.CanStartAutofill, Is.False,
                "The governance row of this kind is switched off - it may not act at all.");
            Assert.That(decision.CanCommit, Is.False);
            Assert.That(decision.BlockedBy, Is.EqualTo(NextPeriodAutonomyBlockedBy.KindDisabled));
        });
    }

    [Test]
    public async Task ResolveAsync_MaxActionHint_ForbidsEvenPreparingAScenario()
    {
        StubAdmins((FirstAdminId, AutonomyLevel.FullyAutonomous));
        StubGovernance(ProactiveMaxAction.Hint, enabled: true, killSwitchActive: false);

        var decision = await _sut.ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(decision.CanStartAutofill, Is.False);
            Assert.That(decision.CanCommit, Is.False);
            Assert.That(decision.BlockedBy, Is.EqualTo(NextPeriodAutonomyBlockedBy.MaxAction));
        });
    }

    [Test]
    public async Task ResolveAsync_MaxActionPrepare_AllowsTheScenarioButNotTheCommit()
    {
        StubAdmins((FirstAdminId, AutonomyLevel.FullyAutonomous));
        StubGovernance(ProactiveMaxAction.Prepare, enabled: true, killSwitchActive: false);

        var decision = await _sut.ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(decision.CanStartAutofill, Is.True);
            Assert.That(decision.CanCommit, Is.False,
                "Prepare means a human accepts the draft; governance may lower the admin consent, never raise it.");
            Assert.That(decision.BlockedBy, Is.EqualTo(NextPeriodAutonomyBlockedBy.MaxAction));
        });
    }

    [Test]
    public async Task ResolveAsync_MaxActionExecuteAndFullAutonomy_OpensBothGates()
    {
        StubAdmins((FirstAdminId, AutonomyLevel.FullyAutonomous));

        var decision = await _sut.ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(decision.CanStartAutofill, Is.True);
            Assert.That(decision.CanCommit, Is.True);
            Assert.That(decision.BlockedBy, Is.EqualTo(NextPeriodAutonomyBlockedBy.None));
        });
    }

    [Test]
    public async Task ResolveAsync_LevelAssisted_ForbidsTheAutofillStartDespiteMaxActionExecute()
    {
        StubAdmins((FirstAdminId, AutonomyLevel.Assisted));

        var decision = await _sut.ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(decision.CanStartAutofill, Is.False,
                "Starting the wizard chain needs Autonomous; MaxAction cannot raise an admin level.");
            Assert.That(decision.CanCommit, Is.False);
            Assert.That(decision.BlockedBy, Is.EqualTo(NextPeriodAutonomyBlockedBy.AutonomyLevel));
        });
    }

    [Test]
    public async Task ResolveAsync_LevelAutonomous_StartsTheChainButNeverCommits()
    {
        StubAdmins((FirstAdminId, AutonomyLevel.Autonomous));

        var decision = await _sut.ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(decision.CanStartAutofill, Is.True);
            Assert.That(decision.CanCommit, Is.False,
                "Level 2 prepares, only level 3 accepts into the real schedule.");
            Assert.That(decision.BlockedBy, Is.EqualTo(NextPeriodAutonomyBlockedBy.AutonomyLevel));
        });
    }

    [Test]
    public async Task ResolveAsync_GlobalLevelAutonomousWithFullyAutonomousAdmins_PreparesWithoutCommitting()
    {
        StubAdmins((FirstAdminId, AutonomyLevel.FullyAutonomous));
        _governanceResolver.GetGlobalAutonomyLevelAsync(Arg.Any<CancellationToken>())
            .Returns(AutonomyLevel.Autonomous);

        var decision = await _sut.ResolveAsync();

        Assert.Multiple(() =>
        {
            Assert.That(decision.EffectiveLevel, Is.EqualTo(AutonomyLevel.Autonomous));
            Assert.That(decision.CanStartAutofill, Is.True);
            Assert.That(decision.CanCommit, Is.False,
                "ProactiveMaxAction collapses global level 2 and 3 onto Execute; the raw level must still separate them, "
                + "otherwise an installation on level 2 would auto-commit.");
            Assert.That(decision.BlockedBy, Is.EqualTo(NextPeriodAutonomyBlockedBy.AutonomyLevel));
        });
    }
}
