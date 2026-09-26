// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PeriodAutoCloseResolver: exactly one configuration may close a period unattended - kill
/// switch off, the period_auto_close rule enabled at Execute, the global level FullyAutonomous and every admin
/// on a stored FullyAutonomous level. Each test takes that configuration and breaks one brake, and the decision
/// must name that brake. The admin minimum runs through the real AdminAutonomyLevelAggregator, like the
/// next-period resolver tests, so the MIN rule under test is the production one.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class PeriodAutoCloseResolverTests
{
    private const string FirstAdminId = "3f1c9a52-0000-0000-0000-000000000001";
    private const string SecondAdminId = "3f1c9a52-0000-0000-0000-000000000002";
    private static readonly Guid GroupId = Guid.Parse("7a1d0000-0000-0000-0000-00000000000a");

    private IPlanningAudienceResolver _audienceResolver = null!;
    private IAgentAutonomyPreferenceRepository _autonomyPreferences = null!;
    private IProactiveGovernanceResolver _governanceResolver = null!;
    private PeriodAutoCloseResolver _sut = null!;

    [SetUp]
    public void Setup()
    {
        _audienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _autonomyPreferences = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _governanceResolver = Substitute.For<IProactiveGovernanceResolver>();
        StubGlobalLevel(AutonomyLevel.FullyAutonomous);
        StubGovernance(ProactiveMaxAction.Execute, enabled: true, killSwitchActive: false);
        StubAdmins((FirstAdminId, AutonomyLevel.FullyAutonomous), (SecondAdminId, AutonomyLevel.FullyAutonomous));

        _sut = new PeriodAutoCloseResolver(
            new AdminAutonomyLevelAggregator(_audienceResolver, _autonomyPreferences),
            _governanceResolver);
    }

    private void StubGlobalLevel(AutonomyLevel level)
    {
        _governanceResolver.GetGlobalAutonomyLevelAsync(Arg.Any<CancellationToken>()).Returns(level);
    }

    /// <summary>
    /// Mirrors ProactiveGovernanceResolver: kill switch and a disabled kind pin EffectiveMaxAction to Hint,
    /// otherwise the configured action is capped by the global level's ladder.
    /// </summary>
    private void StubGovernance(
        ProactiveMaxAction configuredMaxAction,
        bool enabled,
        bool killSwitchActive,
        ProactiveMaxAction globalCap = ProactiveMaxAction.Execute)
    {
        var effective = killSwitchActive || !enabled
            ? ProactiveMaxAction.Hint
            : configuredMaxAction < globalCap ? configuredMaxAction : globalCap;
        _governanceResolver.ResolveAsync(AgentTriggerKinds.PeriodAutoClose, GroupId, Arg.Any<CancellationToken>())
            .Returns(new ProactiveGovernanceDecision(
                TriggerKind: AgentTriggerKinds.PeriodAutoClose,
                GroupId: GroupId,
                EffectiveMaxAction: effective,
                ConfiguredMaxAction: configuredMaxAction,
                Enabled: enabled,
                KillSwitchActive: killSwitchActive,
                DailyActionBudget: ProactiveGovernanceDefaults.DailyActionBudget,
                WindowActionLimit: ProactiveGovernanceDefaults.WindowActionLimit,
                WindowMinutes: ProactiveGovernanceDefaults.WindowMinutes,
                IsStored: true,
                GlobalAutonomyCap: globalCap));
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

    private async Task AssertBlockedAsync(PeriodAutoCloseBlockedBy expected)
    {
        var decision = await _sut.ResolveAsync(GroupId);

        Assert.Multiple(() =>
        {
            Assert.That(decision.CanClose, Is.False);
            Assert.That(decision.BlockedBy, Is.EqualTo(expected));
        });
    }

    [Test]
    public async Task ResolveAsync_EveryGateFullyAutonomousAndExecute_AllowsTheCloseUnderTheFirstAdmin()
    {
        var decision = await _sut.ResolveAsync(GroupId);

        Assert.Multiple(() =>
        {
            Assert.That(decision.CanClose, Is.True);
            Assert.That(decision.BlockedBy, Is.EqualTo(PeriodAutoCloseBlockedBy.None));
            Assert.That(decision.EffectiveLevel, Is.EqualTo(AutonomyLevel.FullyAutonomous));
            Assert.That(decision.DecidingAdminUserId, Is.EqualTo(Guid.Parse(FirstAdminId)));
        });
    }

    [Test]
    public async Task ResolveAsync_ReadsTheGroupScopedRuleOfTheDedicatedKind()
    {
        await _sut.ResolveAsync(GroupId);

        await _governanceResolver.Received(1).ResolveAsync(
            AgentTriggerKinds.PeriodAutoClose, GroupId, Arg.Any<CancellationToken>());
        await _governanceResolver.DidNotReceive().ResolveAsync(
            AgentTriggerKinds.PeriodCloseDue, Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolveAsync_KillSwitchActive_IsBlocked()
    {
        StubGovernance(ProactiveMaxAction.Execute, enabled: true, killSwitchActive: true);

        await AssertBlockedAsync(PeriodAutoCloseBlockedBy.KillSwitch);
    }

    [Test]
    public async Task ResolveAsync_RuleDisabled_IsBlocked()
    {
        StubGovernance(ProactiveMaxAction.Execute, enabled: false, killSwitchActive: false);

        await AssertBlockedAsync(PeriodAutoCloseBlockedBy.KindDisabled);
    }

    [TestCase(ProactiveMaxAction.Hint)]
    [TestCase(ProactiveMaxAction.Prepare)]
    public async Task ResolveAsync_RuleBelowExecute_IsBlocked(ProactiveMaxAction configured)
    {
        StubGovernance(configured, enabled: true, killSwitchActive: false);

        await AssertBlockedAsync(PeriodAutoCloseBlockedBy.MaxAction);
    }

    [TestCase(AutonomyLevel.Propose)]
    [TestCase(AutonomyLevel.Assisted)]
    [TestCase(AutonomyLevel.Autonomous)]
    public async Task ResolveAsync_GlobalLevelBelowFullyAutonomous_IsBlockedEvenWhenTheCapStillSaysExecute(AutonomyLevel globalLevel)
    {
        StubGlobalLevel(globalLevel);

        await AssertBlockedAsync(PeriodAutoCloseBlockedBy.GlobalLevel);
    }

    [TestCase(AutonomyLevel.Propose)]
    [TestCase(AutonomyLevel.Assisted)]
    [TestCase(AutonomyLevel.Autonomous)]
    public async Task ResolveAsync_OneAdminBelowFullyAutonomous_BlocksForEverybody(AutonomyLevel cautiousLevel)
    {
        StubAdmins((FirstAdminId, AutonomyLevel.FullyAutonomous), (SecondAdminId, cautiousLevel));

        await AssertBlockedAsync(PeriodAutoCloseBlockedBy.AdminLevel);
    }

    [Test]
    public async Task ResolveAsync_AnAdminWithoutAStoredLevel_Blocks()
    {
        StubAdmins((FirstAdminId, AutonomyLevel.FullyAutonomous), (SecondAdminId, null));

        await AssertBlockedAsync(PeriodAutoCloseBlockedBy.AdminLevelMissing);
    }

    [Test]
    public async Task ResolveAsync_NoAdmins_Blocks()
    {
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>()).Returns(new HashSet<string>());

        await AssertBlockedAsync(PeriodAutoCloseBlockedBy.NoAdmins);
    }

    [Test]
    public async Task ResolveAsync_AdminIdIsNoGuid_BlocksBecauseTheSealWouldHaveNoAuthor()
    {
        StubAdmins(("not-a-guid", AutonomyLevel.FullyAutonomous));

        await AssertBlockedAsync(PeriodAutoCloseBlockedBy.NoDecidingAdmin);
    }
}
