// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for NextPeriodAutonomyResolver - the MIN-over-admins rule the next-period detector and
/// the auto-commit watcher share, and the deciding admin the audit records for an automatic accept.
/// </summary>

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

        _sut = new NextPeriodAutonomyResolver(_audienceResolver, _autonomyPreferences, _governanceResolver);
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
}
