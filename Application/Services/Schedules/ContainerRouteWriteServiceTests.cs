// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Commands.ContainerTemplates;
using Klacks.Api.Application.Commands.Schedules;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.ContainerTemplates;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Services.RouteOptimization;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Application.Services.Schedules;

/// <summary>
/// ContainerRouteWriteService: optimizes, places visit and travel times, keeps the break between the visits, stores the
/// RouteInfo and writes the full template list under the lock, then verifies; it writes nothing when a visit would be
/// lost or no route exists.
/// </summary>
[TestFixture]
public class ContainerRouteWriteServiceTests
{
    private const int StorageThursday = 4;
    private const string StoredBase = "Hauptsitz";
    private const string BaseAddress = "Bahnhofstrasse 1, 3000 Bern";

    private static readonly Guid ContainerId = Guid.NewGuid();
    private static readonly Guid VisitA = Guid.NewGuid();
    private static readonly Guid VisitB = Guid.NewGuid();
    private static readonly Guid BreakId = Guid.NewGuid();

    private IMediator _mediator = null!;
    private IRouteOptimizationService _optimizer = null!;
    private IContainerBaseAddressResolver _resolver = null!;
    private ContainerRouteWriteService _service = null!;
    private List<ContainerTemplateResource> _templates = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _optimizer = Substitute.For<IRouteOptimizationService>();
        _resolver = Substitute.For<IContainerBaseAddressResolver>();
        _resolver.ResolveAsync(StoredBase, Arg.Any<CancellationToken>()).Returns(BaseAddress);
        _service = new ContainerRouteWriteService(_mediator, _optimizer, _resolver, new ScheduleMapper());

        _templates = [Template()];
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>()).Returns(_ => _templates);
        _mediator.Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>()).Returns(_ => _templates);
        _mediator.Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerLockResource { Id = Guid.NewGuid(), Acquired = true });
        _mediator.Send(Arg.Any<ReleaseContainerLockCommand>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    [Test]
    public async Task WritesVisitAndTravelTimes_PlacesTheBreakByItsStart_AndStoresTheRouteInfo()
    {
        // Arrange
        Optimizer(Route(VisitA, VisitB));

        // Act
        var result = await Run();

        // Assert
        result.Success.ShouldBeTrue(result.Error);
        var items = _templates[0].ContainerTemplateItems;
        items.Select(i => i.ShiftId ?? i.AbsenceId).ShouldBe(new Guid?[] { VisitA, VisitB, BreakId });
        items[0].TimeRangeStartItem.ShouldBe(new TimeOnly(8, 15));
        items[0].TimeRangeEndItem.ShouldBe(new TimeOnly(9, 0));
        items[0].TravelTimeBefore.ShouldBe(new TimeOnly(0, 10));
        items[1].TimeRangeStartItem.ShouldBe(new TimeOnly(9, 17));
        items[1].TravelTimeBefore.ShouldBe(new TimeOnly(0, 7));
        items[1].TravelTimeAfter.ShouldBe(new TimeOnly(0, 12));
        _templates[0].RouteInfo!.StartBase.ShouldBe(BaseAddress);
        _templates[0].RouteInfo!.TravelTimeToEndBase.ShouldBe("00:12:00");
        _templates[0].RouteInfo!.OptimizedRoute.Count.ShouldBe(4);
        result.Stops.Select(s => s.VisitStart).ShouldBe(new[] { "08:15", "09:17" });
        result.BreakCount.ShouldBe(1);
        await _optimizer.Received(1).OptimizeRouteByShiftIdsAsync(
            Arg.Is<List<Guid>>(ids => ids.SequenceEqual(new[] { VisitA, VisitB })),
            BaseAddress,
            BaseAddress,
            ContainerTransportMode.ByCar,
            Arg.Is<List<TimeBlock>>(b => b.Count == 1 && b[0].Id == BreakId && !b[0].IsMovable),
            new TimeOnly(8, 0));
        await _mediator.Received(1).Send(
            Arg.Is<PutContainerTemplatesCommand>(c => c.ContainerId == ContainerId && c.Resources == _templates),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task VisitMissingFromTheRoute_WritesNothing()
    {
        // Arrange
        Optimizer(Route(VisitA));

        // Act
        var result = await Run();

        // Assert
        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("1 of 2");
        await _mediator.DidNotReceive().Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task VisitWithoutItsShift_WritesNothing()
    {
        // Arrange
        Optimizer(Route(VisitA, VisitB));
        _templates[0].ContainerTemplateItems.Single(i => i.ShiftId == VisitB).Shift = null;

        // Act
        var result = await Run();

        // Assert
        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("1 of 2");
        await _mediator.DidNotReceive().Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task SameTaskListedTwice_IsRefusedBeforeOptimizing()
    {
        // Arrange
        _templates[0].ContainerTemplateItems.Add(Visit(VisitA));

        // Act
        var result = await Run();

        // Assert
        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("more than once");
        await _optimizer.DidNotReceiveWithAnyArgs().OptimizeRouteByShiftIdsAsync(default!);
        await _mediator.DidNotReceive().Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TravelOfADayOrMore_WritesNothing()
    {
        // Arrange
        var route = Route(VisitA, VisitB);
        route.DurationMatrix[0, 1] = 86399;
        Optimizer(route);

        // Act
        var result = await Run();

        // Assert
        result.Success.ShouldBeFalse();
        result.Error!.ShouldContain("24 hours");
        await _mediator.DidNotReceive().Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task NoRoute_WritesNothing()
    {
        // Arrange
        Optimizer(new RouteOptimizationResult(
            [], 0, TimeSpan.Zero, new double[0, 0], new double[0, 0], TimeSpan.Zero, [], 0, 0, TimeSpan.Zero, []));

        // Act
        var result = await Run();

        // Assert
        result.Success.ShouldBeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FewerThanTwoVisits_IsRefusedBeforeOptimizing()
    {
        // Arrange
        _templates[0].ContainerTemplateItems.RemoveAll(i => i.ShiftId == VisitB);

        // Act
        var result = await Run();

        // Assert
        result.Success.ShouldBeFalse();
        await _optimizer.DidNotReceiveWithAnyArgs().OptimizeRouteByShiftIdsAsync(default!);
    }

    [Test]
    public async Task LockHeldByAnotherUser_ReadsNothing()
    {
        // Arrange
        _mediator.Send(Arg.Any<AcquireContainerLockCommand>(), Arg.Any<CancellationToken>())
            .Returns(new ContainerLockResource { Acquired = false });

        // Act
        var result = await Run();

        // Assert
        result.Success.ShouldBeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LockLostBeforeTheWrite_IsReported()
    {
        // Arrange
        Optimizer(Route(VisitA, VisitB));
        _mediator.Send(Arg.Any<PutContainerTemplatesCommand>(), Arg.Any<CancellationToken>())
            .Returns<List<ContainerTemplateResource>>(_ => throw new ContainerLockedException("not locked"));

        // Act
        var result = await Run();

        // Assert
        result.Success.ShouldBeFalse();
        result.Error.ShouldBe("not locked");
    }

    [Test]
    public async Task ReReadWithOtherTimes_FailsVerification()
    {
        // Arrange
        Optimizer(Route(VisitA, VisitB));
        var calls = 0;
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>())
            .Returns(_ => ++calls == 1 ? _templates : [Template()]);

        // Act
        var result = await Run();

        // Assert
        result.Success.ShouldBeFalse();
        result.Error!.ShouldStartWith("Verification failed");
    }

    private Task<ContainerRouteWriteResult> Run() =>
        _service.OptimizeAndWriteAsync(ContainerId, StorageThursday, false, "tab-1", CancellationToken.None);

    private void Optimizer(RouteOptimizationResult result) =>
        _optimizer.OptimizeRouteByShiftIdsAsync(
                Arg.Any<List<Guid>>(), Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<ContainerTransportMode>(),
                Arg.Any<List<TimeBlock>?>(), Arg.Any<TimeOnly?>())
            .Returns(result);

    private static RouteOptimizationResult Route(params Guid[] visits)
    {
        var locations = new List<Location> { new() { Name = "Base", Address = BaseAddress, ShiftId = Guid.Empty } };
        locations.AddRange(visits.Select((id, i) => new Location { Name = $"Client {i + 1}", Address = $"Street {i + 1}", ShiftId = id }));
        locations.Add(new Location { Name = "Base", Address = BaseAddress, ShiftId = Guid.Empty });

        var size = locations.Count;
        var distances = new double[size, size];
        var durations = new double[size, size];
        var legSeconds = new[] { 600.0, 420.0, 720.0 };
        for (var i = 0; i < size - 1; i++)
        {
            distances[i, i + 1] = 1.5;
            durations[i, i + 1] = i == size - 2 ? 720.0 : legSeconds[Math.Min(i, 1)];
        }

        var indices = Enumerable.Range(0, size).ToList();
        return new RouteOptimizationResult(
            locations, 6.0, TimeSpan.FromMinutes(120), distances, durations, TimeSpan.FromMinutes(10), indices,
            1.5, 3.5, TimeSpan.FromMinutes(12), indices);
    }

    private static ContainerTemplateResource Template() => new()
    {
        Id = Guid.NewGuid(),
        ContainerId = ContainerId,
        Weekday = StorageThursday,
        FromTime = new TimeOnly(8, 0),
        UntilTime = new TimeOnly(17, 0),
        StartBase = StoredBase,
        EndBase = StoredBase,
        ContainerTemplateItems =
        [
            Visit(VisitA),
            new ContainerTemplateItemResource
            {
                Id = Guid.NewGuid(),
                AbsenceId = BreakId,
                StartItem = new TimeOnly(12, 0),
                EndItem = new TimeOnly(12, 30)
            },
            Visit(VisitB)
        ]
    };

    private static ContainerTemplateItemResource Visit(Guid shiftId) => new()
    {
        Id = Guid.NewGuid(),
        ShiftId = shiftId,
        StartItem = new TimeOnly(8, 0),
        EndItem = new TimeOnly(17, 0),
        BriefingTime = new TimeOnly(0, 5),
        DebriefingTime = new TimeOnly(0, 5),
        TimeRangeStartItem = new TimeOnly(8, 0),
        TimeRangeEndItem = new TimeOnly(17, 0),
        Shift = new ShiftResource { Id = shiftId, WorkTime = 0.75m, IsTimeRange = true }
    };
}
