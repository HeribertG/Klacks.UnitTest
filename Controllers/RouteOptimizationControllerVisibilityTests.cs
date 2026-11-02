// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Visibility of the RouteOptimization endpoints. POST optimize-route hands the optimizer only the ids the visibility filter
/// let through. GET distance-matrix and POST autofill answer a hidden container like a missing one (the middleware maps the
/// KeyNotFoundException to 404, so it must leave the action) and POST autofill plans only with shifts the caller may read:
/// the real filter, token guard, container guard and limiter run here, only their readers are stubbed, because "a key shift
/// is left out for every caller" would prove nothing against a stubbed filter.
/// </summary>

using Klacks.Api.Application.DTOs.RouteOptimization;
using Klacks.Api.Application.Services.Groups;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Services.RouteOptimization;
using Klacks.Api.Domain.Services.RouteOptimization.ContainerAutofill;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Presentation.Controllers.UserBackend;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class RouteOptimizationControllerVisibilityTests
{
    private const int Weekday = 4;
    private const string FromTime = "08:00";
    private const string UntilTime = "17:00";
    private const string Base = "Bahnhofstrasse 1, Bern";

    private static readonly Guid ContainerId = Guid.NewGuid();

    private IRouteOptimizationService _routeService = null!;
    private IContainerAutofillService _autofillService = null!;
    private IContainerAvailableTasksService _availableTasks = null!;
    private IShiftVisibilityReader _reader = null!;
    private IAnalyseScenarioRepository _scenarios = null!;
    private IGroupVisibilityGuard _groupGuard = null!;

    [SetUp]
    public void SetUp()
    {
        _routeService = Substitute.For<IRouteOptimizationService>();
        _autofillService = Substitute.For<IContainerAutofillService>();
        _autofillService.AutofillAsync(Arg.Any<ContainerAutofillRequest>())
            .Returns(ContainerAutofillResultBuilder.CreateEmptyResult(ContainerTransportMode.ByCar));
        _availableTasks = Substitute.For<IContainerAvailableTasksService>();
        StubCandidates();
        _reader = Substitute.For<IShiftVisibilityReader>();
        _reader.GetTokenedShiftWorldsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<ShiftWorld>());
        _scenarios = Substitute.For<IAnalyseScenarioRepository>();
        _groupGuard = Substitute.For<IGroupVisibilityGuard>();
        _groupGuard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(false);
    }

    private void StubCandidates(params Guid[] taskIds)
    {
        _availableTasks.GetAvailableTasksAsync(
                Arg.Any<Guid>(), Arg.Any<int>(), Arg.Any<TimeOnly>(), Arg.Any<TimeOnly>(), Arg.Any<string?>(), Arg.Any<Guid?>(),
                Arg.Any<bool?>(), Arg.Any<bool?>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<IReadOnlyCollection<Guid>?>(),
                Arg.Any<CancellationToken>())
            .Returns(taskIds.Select(id => new Shift { Id = id }).ToList());
    }

    private void StubWorlds(params ShiftWorld[] worlds)
    {
        _reader.GetTokenedShiftWorldsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => worlds.Where(world => call.ArgAt<IReadOnlyCollection<Guid>>(0).Contains(world.ShiftId)).ToList());
    }

    private Guid StubScenario(AnalyseScenarioKind kind, Guid groupId, bool groupVisible)
    {
        var token = Guid.NewGuid();
        _scenarios.GetByTokenAsync(token, Arg.Any<CancellationToken>())
            .Returns(new AnalyseScenario { Token = token, Kind = kind, GroupId = groupId });
        _groupGuard.IsGroupVisibleAsync(groupId, Arg.Any<CancellationToken>()).Returns(groupVisible);
        return token;
    }

    private RouteOptimizationController Controller(IShiftVisibilityGuard shiftGuard, IRouteShiftVisibilityFilter? stubbedFilter = null)
    {
        var filter = stubbedFilter ?? new RouteShiftVisibilityFilter(
            shiftGuard, _reader, new ScenarioTokenAccessGuard(_scenarios, _groupGuard));

        return new RouteOptimizationController(
            _routeService,
            _autofillService,
            filter,
            new ContainerVisibilityGuard(shiftGuard, _reader, NullLogger<ContainerVisibilityGuard>.Instance),
            new ContainerAutofillRequestLimiter(_availableTasks, shiftGuard, filter),
            Substitute.For<IMediator>(),
            NullLogger<RouteOptimizationController>.Instance);
    }

    private static AutofillRequestDto AutofillRequest(params Guid[] overrideIds) => new()
    {
        ContainerId = ContainerId,
        Weekday = Weekday,
        StartBase = Base,
        EndBase = Base,
        FromTime = FromTime,
        UntilTime = UntilTime,
        AdditionalAvailableWorkIds = [.. overrideIds]
    };

    private Task PlannedWith(Func<ContainerAutofillRequest, bool> expected) =>
        _autofillService.Received(1).AutofillAsync(Arg.Is<ContainerAutofillRequest>(request => expected(request)));

    [Test]
    public async Task OptimizeRoute_PassesOnlyTheIdsTheFilterLetThrough()
    {
        var visible = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        var filter = Substitute.For<IRouteShiftVisibilityFilter>();
        filter.FilterAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns([visible]);
        _routeService.OptimizeRouteByShiftIdsAsync(
                Arg.Any<List<Guid>>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<ContainerTransportMode>(),
                Arg.Any<List<TimeBlock>?>(), Arg.Any<TimeOnly?>())
            .Returns(new RouteOptimizationResult(
                [], 0.0, TimeSpan.Zero, new double[0, 0], new double[0, 0], TimeSpan.Zero, [], 0.0, 0.0, TimeSpan.Zero, []));

        await Controller(ShiftVisibilityGuards.AllowAll(), filter)
            .OptimizeRoute(new OptimizeRouteRequestDto { ShiftIds = [visible, hidden] });

        await _routeService.Received(1).OptimizeRouteByShiftIdsAsync(
            Arg.Is<List<Guid>>(ids => ids.SequenceEqual(new[] { visible })),
            Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<ContainerTransportMode>(),
            Arg.Any<List<TimeBlock>?>(), Arg.Any<TimeOnly?>());
    }

    [Test]
    public async Task DistanceMatrix_AHiddenContainer_IsAnsweredAsMissing_AndNothingIsRead()
    {
        var controller = Controller(ShiftVisibilityGuards.Hiding(ContainerId));

        await Should.ThrowAsync<KeyNotFoundException>(() => controller.GetDistanceMatrix(ContainerId, Weekday));

        await _routeService.DidNotReceiveWithAnyArgs().CalculateDistanceMatrixAsync(default, default, default, default);
    }

    [Test]
    public async Task DistanceMatrix_AVisibleContainer_IsAnswered()
    {
        _routeService.CalculateDistanceMatrixAsync(ContainerId, Weekday, false, ContainerTransportMode.ByCar)
            .Returns(new DistanceMatrix([], new double[0, 0], new double[0, 0]));

        var result = await Controller(ShiftVisibilityGuards.AllowAll()).GetDistanceMatrix(ContainerId, Weekday);

        result.Result.ShouldBeOfType<OkObjectResult>();
    }

    [Test]
    public async Task Autofill_AHiddenContainer_IsAnsweredAsMissing_AndNothingIsPlanned()
    {
        StubCandidates(Guid.NewGuid());
        var controller = Controller(ShiftVisibilityGuards.Hiding(ContainerId));

        await Should.ThrowAsync<KeyNotFoundException>(() => controller.Autofill(AutofillRequest()));

        await _autofillService.DidNotReceiveWithAnyArgs().AutofillAsync(default!);
    }

    [Test]
    public async Task Autofill_AHiddenOverride_IsLeftOut()
    {
        var visible = Guid.NewGuid();
        var hidden = Guid.NewGuid();

        await Controller(ShiftVisibilityGuards.Hiding(hidden)).Autofill(AutofillRequest(visible, hidden));

        await PlannedWith(request => request.AdditionalAvailableWorkIds!.SequenceEqual(new[] { visible }));
    }

    [Test]
    public async Task Autofill_AHiddenAndAMissingOverride_GetTheSameAnswer()
    {
        var hidden = Guid.NewGuid();
        var missing = Guid.NewGuid();
        var controller = Controller(ShiftVisibilityGuards.Hiding(hidden, missing));

        var hiddenAnswer = await controller.Autofill(AutofillRequest(hidden));
        var missingAnswer = await controller.Autofill(AutofillRequest(missing));

        hiddenAnswer.Result.ShouldBeOfType<OkObjectResult>();
        missingAnswer.Result.ShouldBeOfType<OkObjectResult>();
        await _autofillService.Received(2).AutofillAsync(
            Arg.Is<ContainerAutofillRequest>(request => request.AdditionalAvailableWorkIds!.Count == 0));
    }

    [Test]
    public async Task Autofill_AKeyShift_IsLeftOutForARestrictedCaller()
    {
        var keyShift = Guid.NewGuid();
        var keyToken = StubScenario(AnalyseScenarioKind.Variant, Guid.NewGuid(), groupVisible: true);
        StubWorlds(new ShiftWorld(keyShift, keyToken, true));

        await Controller(ShiftVisibilityGuards.AllowAll()).Autofill(AutofillRequest(keyShift));

        await PlannedWith(request => request.AdditionalAvailableWorkIds!.Count == 0);
    }

    [Test]
    public async Task Autofill_AKeyShift_IsLeftOutForAnUnrestrictedCaller_TheCloneOfAVisibleScenarioStays()
    {
        var keyShift = Guid.NewGuid();
        var clone = Guid.NewGuid();
        var keyToken = StubScenario(AnalyseScenarioKind.Variant, Guid.NewGuid(), groupVisible: true);
        var scenarioToken = StubScenario(AnalyseScenarioKind.Scenario, Guid.NewGuid(), groupVisible: true);
        StubWorlds(new ShiftWorld(keyShift, keyToken, true), new ShiftWorld(clone, scenarioToken, false));
        _groupGuard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(true);

        await Controller(ShiftVisibilityGuards.AllowAll()).Autofill(AutofillRequest(keyShift, clone));

        await PlannedWith(request => request.AdditionalAvailableWorkIds!.SequenceEqual(new[] { clone }));
    }

    [Test]
    public async Task Autofill_TheCloneOfAVisibleScenarioStays_TheCloneOfAHiddenOneIsLeftOut()
    {
        var clone = Guid.NewGuid();
        var hiddenClone = Guid.NewGuid();
        var visibleToken = StubScenario(AnalyseScenarioKind.Scenario, Guid.NewGuid(), groupVisible: true);
        var hiddenToken = StubScenario(AnalyseScenarioKind.Scenario, Guid.NewGuid(), groupVisible: false);
        StubWorlds(new ShiftWorld(clone, visibleToken, false), new ShiftWorld(hiddenClone, hiddenToken, false));

        await Controller(ShiftVisibilityGuards.AllowAll()).Autofill(AutofillRequest(clone, hiddenClone));

        await PlannedWith(request => request.AdditionalAvailableWorkIds!.SequenceEqual(new[] { clone }));
    }

    [Test]
    public async Task Autofill_TheCandidatesOfTheSlot_AreLimitedToTheVisibleOnes_BeforeTheRouteIsPlanned()
    {
        var visible = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        StubCandidates(visible, hidden);

        await Controller(ShiftVisibilityGuards.Hiding(hidden)).Autofill(AutofillRequest());

        await PlannedWith(request => request.RestrictToShiftIds!.SequenceEqual(new[] { visible }));
    }

    [Test]
    public async Task Autofill_WithoutAVisibleCandidate_TheRestrictionStaysEmpty_NotOpen()
    {
        var hidden = Guid.NewGuid();
        StubCandidates(hidden);

        await Controller(ShiftVisibilityGuards.Hiding(hidden)).Autofill(AutofillRequest());

        await PlannedWith(request => request.RestrictToShiftIds is { Count: 0 });
    }
}
