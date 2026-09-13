// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AdminAutonomyLevelAggregator - the single "the most cautious admin brakes for
/// everyone" rule the next-period automation and the goal-plan execution now share. Covers the MIN
/// over admins, the stable ordinal tie-break the audit depends on, and both missing-row policies: the
/// next-period path reads AutonomyDefaults.DefaultLevel for an admin who never stored a level, the
/// unattended goal-plan path refuses to run at all.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Services.Assistant.Autonomy;

[TestFixture]
public class AdminAutonomyLevelAggregatorTests
{
    private const string FirstAdminId = "7b2c9a52-0000-0000-0000-000000000001";
    private const string SecondAdminId = "7b2c9a52-0000-0000-0000-000000000002";
    private const string ThirdAdminId = "7b2c9a52-0000-0000-0000-000000000003";

    private IPlanningAudienceResolver _audienceResolver = null!;
    private IAgentAutonomyPreferenceRepository _autonomyPreferences = null!;
    private AdminAutonomyLevelAggregator _sut = null!;

    [SetUp]
    public void Setup()
    {
        _audienceResolver = Substitute.For<IPlanningAudienceResolver>();
        _autonomyPreferences = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _sut = new AdminAutonomyLevelAggregator(_audienceResolver, _autonomyPreferences);
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
    public async Task AggregateAsync_NoAdmins_ProducesNoLevelAtAll()
    {
        _audienceResolver.GetAdminUserIdsAsync(Arg.Any<CancellationToken>()).Returns(new HashSet<string>());

        var aggregate = await _sut.AggregateAsync(AdminAutonomyMissingPreferencePolicy.FallBackToDefault);

        Assert.Multiple(() =>
        {
            Assert.That(aggregate.MinimumLevel, Is.Null);
            Assert.That(aggregate.DecidingAdminUserId, Is.Null);
            Assert.That(aggregate.AdminWithoutStoredLevel, Is.Null,
                "No admin at all is not the same finding as one admin missing a row.");
        });
    }

    [Test]
    public async Task AggregateAsync_OneCautiousAdmin_IsTheMinimumAndIsNamed()
    {
        StubAdmins(
            (FirstAdminId, AutonomyLevel.FullyAutonomous),
            (SecondAdminId, AutonomyLevel.Assisted),
            (ThirdAdminId, AutonomyLevel.Autonomous));

        var aggregate = await _sut.AggregateAsync(AdminAutonomyMissingPreferencePolicy.FallBackToDefault);

        Assert.Multiple(() =>
        {
            Assert.That(aggregate.MinimumLevel, Is.EqualTo(AutonomyLevel.Assisted));
            Assert.That(aggregate.DecidingAdminUserId, Is.EqualTo(Guid.Parse(SecondAdminId)));
        });
    }

    [Test]
    public async Task AggregateAsync_AllAdminsAtTheSameLevel_NamesTheOrdinallyFirstAdmin()
    {
        StubAdmins(
            (ThirdAdminId, AutonomyLevel.Autonomous),
            (SecondAdminId, AutonomyLevel.Autonomous),
            (FirstAdminId, AutonomyLevel.Autonomous));

        var aggregate = await _sut.AggregateAsync(AdminAutonomyMissingPreferencePolicy.FallBackToDefault);

        Assert.That(aggregate.DecidingAdminUserId, Is.EqualTo(Guid.Parse(FirstAdminId)),
            "The admin set is unordered; without a stable tie-break the audit would name a different admin per run.");
    }

    [Test]
    public async Task AggregateAsync_FallBackPolicy_ReadsAMissingRowAsTheDefaultLevel()
    {
        StubAdmins((FirstAdminId, null));

        var aggregate = await _sut.AggregateAsync(AdminAutonomyMissingPreferencePolicy.FallBackToDefault);

        Assert.Multiple(() =>
        {
            Assert.That(aggregate.MinimumLevel, Is.EqualTo(AutonomyDefaults.DefaultLevel));
            Assert.That(aggregate.AdminWithoutStoredLevel, Is.Null);
        });
    }

    [Test]
    public async Task AggregateAsync_BlockPolicy_RefusesAndNamesTheAdminWithoutARow()
    {
        StubAdmins((FirstAdminId, AutonomyLevel.FullyAutonomous), (SecondAdminId, null));

        var aggregate = await _sut.AggregateAsync(AdminAutonomyMissingPreferencePolicy.Block);

        Assert.Multiple(() =>
        {
            Assert.That(aggregate.MinimumLevel, Is.Null,
                "Nobody chose the shared default, so an unattended path may not borrow it.");
            Assert.That(aggregate.AdminWithoutStoredLevel, Is.EqualTo(SecondAdminId));
        });
    }

    [Test]
    public async Task AggregateAsync_BlockPolicy_WithEveryRowStored_StillAggregatesTheMinimum()
    {
        StubAdmins((FirstAdminId, AutonomyLevel.FullyAutonomous), (SecondAdminId, AutonomyLevel.Autonomous));

        var aggregate = await _sut.AggregateAsync(AdminAutonomyMissingPreferencePolicy.Block);

        Assert.Multiple(() =>
        {
            Assert.That(aggregate.MinimumLevel, Is.EqualTo(AutonomyLevel.Autonomous));
            Assert.That(aggregate.DecidingAdminUserId, Is.EqualTo(Guid.Parse(SecondAdminId)));
            Assert.That(aggregate.AdminWithoutStoredLevel, Is.Null);
        });
    }

    [Test]
    public async Task AggregateAsync_AdminIdThatIsNotAGuid_ReportsNoDeciderRatherThanEmptyGuid()
    {
        StubAdmins(("not-a-guid", AutonomyLevel.Assisted));

        var aggregate = await _sut.AggregateAsync(AdminAutonomyMissingPreferencePolicy.FallBackToDefault);

        Assert.Multiple(() =>
        {
            Assert.That(aggregate.MinimumLevel, Is.EqualTo(AutonomyLevel.Assisted));
            Assert.That(aggregate.DecidingAdminUserId, Is.Null);
        });
    }
}
