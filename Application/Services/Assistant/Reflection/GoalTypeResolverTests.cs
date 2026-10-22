// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The goal type a candidate shows and drafts from is derived from the installation's state at the moment it is
/// read, never from what was stored when the candidate was proposed: a "no schedule yet" proposal stored before
/// the setup stage existed must still name the next missing step, and the "seal open orders" goal and the setup
/// goal must never show side by side.
/// </summary>

namespace Klacks.UnitTest.Application.Services.Assistant.Reflection;

using Klacks.Api.Application.Services.Assistant.Reflection;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;

[TestFixture]
public class GoalTypeResolverTests
{
    private IScheduleActivityProbe _probe = null!;
    private GoalTypeResolver _sut = null!;

    [SetUp]
    public void Setup()
    {
        _probe = Substitute.For<IScheduleActivityProbe>();
        _sut = new GoalTypeResolver(_probe);
    }

    private void GivenState(bool hasOrders, bool hasShifts, bool hasWork) =>
        _probe.GetSetupStateAsync(Arg.Any<CancellationToken>())
            .Returns(new ScheduleSetupState(hasOrders, hasShifts, hasWork, true, true));

    [Test]
    public async Task ResolveAsync_NoScheduleYetWithOrdersButNoShifts_ResolvesTheSealGoal()
    {
        GivenState(hasOrders: true, hasShifts: false, hasWork: false);

        var result = await _sut.ResolveAsync(AgentTriggerKinds.NoScheduleYet, CancellationToken.None);

        result.IsVisible.ShouldBeTrue();
        result.Definition.ShouldBeSameAs(GoalTypeCatalog.FindForStage(ScheduleSetupStage.OrdersButNoShifts));
    }

    [Test]
    public async Task ResolveAsync_NoScheduleYetWithoutOrders_ResolvesTheFirstOrdersGoal()
    {
        GivenState(hasOrders: false, hasShifts: false, hasWork: false);

        var result = await _sut.ResolveAsync(AgentTriggerKinds.NoScheduleYet, CancellationToken.None);

        result.Definition.ShouldBeSameAs(GoalTypeCatalog.FindForStage(ScheduleSetupStage.NothingYet));
    }

    [Test]
    public async Task ResolveAsync_NoScheduleYetWithShiftsButNoWork_ResolvesTheSchedulingGoal()
    {
        GivenState(hasOrders: true, hasShifts: true, hasWork: false);

        var result = await _sut.ResolveAsync(AgentTriggerKinds.NoScheduleYet, CancellationToken.None);

        result.Definition.ShouldBeSameAs(GoalTypeCatalog.ByTriggerKind[AgentTriggerKinds.NoScheduleYet]);
    }

    [Test]
    public async Task ResolveAsync_NoScheduleYetOnceWorkExists_IsHiddenAsStale()
    {
        GivenState(hasOrders: true, hasShifts: true, hasWork: true);

        var result = await _sut.ResolveAsync(AgentTriggerKinds.NoScheduleYet, CancellationToken.None);

        result.IsVisible.ShouldBeFalse();
    }

    [Test]
    public async Task ResolveAsync_OpenOrderWithoutAnyPlannableShift_IsHiddenBehindTheSetupGoal()
    {
        GivenState(hasOrders: true, hasShifts: false, hasWork: false);

        var result = await _sut.ResolveAsync(AgentTriggerKinds.OpenOrder, CancellationToken.None);

        result.IsVisible.ShouldBeFalse();
    }

    [Test]
    public async Task ResolveAsync_OpenOrderOncePlannableShiftsExist_IsVisible()
    {
        GivenState(hasOrders: true, hasShifts: true, hasWork: true);

        var result = await _sut.ResolveAsync(AgentTriggerKinds.OpenOrder, CancellationToken.None);

        result.IsVisible.ShouldBeTrue();
        result.Definition.ShouldBeSameAs(GoalTypeCatalog.ByTriggerKind[AgentTriggerKinds.OpenOrder]);
    }

    [Test]
    public async Task ResolveAsync_StageIndependentGoal_NeverProbesTheInstallation()
    {
        var result = await _sut.ResolveAsync(AgentTriggerKinds.UnstaffedShift, CancellationToken.None);

        result.Definition.ShouldBeSameAs(GoalTypeCatalog.ByTriggerKind[AgentTriggerKinds.UnstaffedShift]);
        await _probe.DidNotReceive().GetSetupStateAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolveAsync_CandidateFromBeforeTheCatalogue_StaysVisibleWithoutDefinition()
    {
        var result = await _sut.ResolveAsync(null, CancellationToken.None);

        result.IsVisible.ShouldBeTrue();
        result.Definition.ShouldBeNull();
    }

    [Test]
    public async Task ResolveAsync_SeveralCandidates_ProbeTheInstallationOnce()
    {
        GivenState(hasOrders: true, hasShifts: false, hasWork: false);

        await _sut.ResolveAsync(AgentTriggerKinds.NoScheduleYet, CancellationToken.None);
        await _sut.ResolveAsync(AgentTriggerKinds.OpenOrder, CancellationToken.None);

        await _probe.Received(1).GetSetupStateAsync(Arg.Any<CancellationToken>());
    }
}
