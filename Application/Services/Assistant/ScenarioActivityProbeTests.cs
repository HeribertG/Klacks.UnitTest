// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ScenarioActivityProbe: the state signal of the scenario follow-up guarantee. Visible = inside the turn user's
/// group scope, the rule of GetActiveAutofillRunsQueryHandler and ListScenariosSkill; a group-less run only counts
/// for an unrestricted user, an empty user sees nothing, an open scenario only counts inside the recency window,
/// and the cheap unscoped check runs before any scope is resolved.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Schedules.AutoWizard;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.UnitTest.TestHelpers;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class ScenarioActivityProbeTests
{
    private static readonly Guid UserId = Guid.NewGuid();
    private static readonly Guid VisibleRootId = Guid.NewGuid();
    private static readonly Guid VisibleGroupId = Guid.NewGuid();
    private static readonly Guid ForeignGroupId = Guid.NewGuid();
    private static readonly DateTimeOffset Now = new(2026, 10, 15, 12, 0, 0, TimeSpan.Zero);

    private IAutofillRunPresenceTracker _presence = null!;
    private IAnalyseScenarioRepository _scenarios = null!;
    private IGroupVisibilityService _visibility = null!;

    [SetUp]
    public void SetUp()
    {
        _presence = Substitute.For<IAutofillRunPresenceTracker>();
        _presence.GetActive().Returns([]);
        _scenarios = Substitute.For<IAnalyseScenarioRepository>();
        _scenarios.AnyOpenVisibleAsync(default, default, default).ReturnsForAnyArgs(false);
        _visibility = Substitute.For<IGroupVisibilityService>();
        GivenScope(GroupVisibilityScope.Restricted([VisibleRootId], [VisibleRootId, VisibleGroupId]));
    }

    private void GivenScope(GroupVisibilityScope scope) =>
        _visibility.GetVisibilityScopeForUserAsync(UserId.ToString(), Arg.Any<CancellationToken>()).Returns(scope);

    private void GivenRunningRun(Guid? groupId) =>
        _presence.GetActive().Returns(
        [
            new AutofillRunPresenceDto(Guid.NewGuid(), groupId, new DateOnly(2026, 10, 1), new DateOnly(2026, 10, 31), null)
        ]);

    private void GivenOpenScenarioUnscoped() =>
        _scenarios.AnyOpenVisibleAsync(null, Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);

    private void GivenOpenScenarioInVisibleRoots() =>
        _scenarios.AnyOpenVisibleAsync(
            Arg.Is<IReadOnlyCollection<Guid>?>(roots => roots != null && roots.Contains(VisibleRootId)),
            Arg.Any<DateTime>(), Arg.Any<CancellationToken>()).Returns(true);

    private Task<bool> Probe(Guid? userId = null) =>
        new ScenarioActivityProbe(_presence, _scenarios, _visibility, new SettableTimeProvider(Now.UtcDateTime))
            .HasVisibleActivityAsync(userId ?? UserId);

    [Test]
    public async Task NothingOpenAndNothingRunning_IsNoActivity_WithoutResolvingAScope()
    {
        (await Probe()).ShouldBeFalse();
        await _visibility.DidNotReceiveWithAnyArgs().GetVisibilityScopeForUserAsync(default, default);
    }

    [Test]
    public async Task TheRecencyWindowIsTheScenarioPendingDetectorMinimum()
    {
        await Probe();

        var expectedSince = Now.UtcDateTime.AddHours(-ScenarioPendingDetector.MinimumPendingHours);
        await _scenarios.Received(1).AnyOpenVisibleAsync(null, expectedSince, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ARunInAVisibleGroup_IsActivity_WithoutAScenarioQuery()
    {
        GivenRunningRun(VisibleGroupId);

        (await Probe()).ShouldBeTrue();
        await _scenarios.DidNotReceiveWithAnyArgs().AnyOpenVisibleAsync(default, default, default);
    }

    [Test]
    public async Task ARunInAForeignGroup_IsNoActivity_WhenNoVisibleScenarioIsOpenEither()
    {
        GivenRunningRun(ForeignGroupId);

        (await Probe()).ShouldBeFalse();
    }

    [Test]
    public async Task AGroupLessRun_CountsOnlyForAnUnrestrictedUser()
    {
        GivenRunningRun(null);
        (await Probe()).ShouldBeFalse();

        GivenScope(GroupVisibilityScope.Unrestricted());
        (await Probe()).ShouldBeTrue();
    }

    [Test]
    public async Task AnOpenScenarioInsideTheScope_IsActivity_AndTheSecondQueryIsScopedToTheVisibleRoots()
    {
        GivenOpenScenarioUnscoped();
        GivenOpenScenarioInVisibleRoots();

        (await Probe()).ShouldBeTrue();
        await _scenarios.Received(1).AnyOpenVisibleAsync(
            Arg.Is<IReadOnlyCollection<Guid>?>(roots => roots != null && roots.Contains(VisibleRootId)),
            Arg.Any<DateTime>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnOpenScenarioOutsideTheScope_IsNoActivity()
    {
        GivenOpenScenarioUnscoped();

        (await Probe()).ShouldBeFalse();
    }

    [Test]
    public async Task AnUnrestrictedUser_ReusesTheUnscopedAnswer_WithoutASecondQuery()
    {
        GivenScope(GroupVisibilityScope.Unrestricted());
        GivenOpenScenarioUnscoped();

        (await Probe()).ShouldBeTrue();
        await _scenarios.ReceivedWithAnyArgs(1).AnyOpenVisibleAsync(default, default, default);
    }

    [Test]
    public async Task AUserWithoutVisibleGroups_SeesNothing_EvenWithARunAndAnOpenScenario()
    {
        GivenScope(GroupVisibilityScope.Restricted([], []));
        GivenRunningRun(VisibleGroupId);
        GivenOpenScenarioUnscoped();

        (await Probe()).ShouldBeFalse();
        await _scenarios.DidNotReceiveWithAnyArgs().AnyOpenVisibleAsync(default, default, default);
    }

    [Test]
    public async Task AnEmptyUserId_SeesNothing_WithoutAnyLookup()
    {
        GivenRunningRun(null);

        (await Probe(Guid.Empty)).ShouldBeFalse();
        await _visibility.DidNotReceiveWithAnyArgs().GetVisibilityScopeForUserAsync(default, default);
        await _scenarios.DidNotReceiveWithAnyArgs().AnyOpenVisibleAsync(default, default, default);
    }
}
