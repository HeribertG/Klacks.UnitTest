// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// WeeklyTourPlacementVerifier re-reads the templates of every written container and lists a service of another tour, a
/// template that does not hold exactly what was reported as placed, a service stored twice and a weekday whose placed and
/// not-placed services do not add up; a consistent result yields no mismatch.
/// </summary>

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Queries.ContainerTemplates;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Services.Schedules;

[TestFixture]
public class WeeklyTourPlacementVerifierTests
{
    private static readonly Guid OwnA = Guid.NewGuid();
    private static readonly Guid OwnB = Guid.NewGuid();
    private static readonly Guid Foreign = Guid.NewGuid();
    private static readonly Guid ContainerId = Guid.NewGuid();

    private IMediator _mediator = null!;
    private WeeklyTourPlacementVerifier _verifier = null!;

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _verifier = new WeeklyTourPlacementVerifier(_mediator);
    }

    private static WeeklyTourPlan Plan() => new(
        [
            new PlannedWeeklyTour(1, [Guid.NewGuid()], new Dictionary<int, List<Shift>>
            {
                [1] = [new Shift { Id = OwnA }, new Shift { Id = OwnB }]
            })
        ],
        new TimeOnly(8, 0), new TimeOnly(17, 0), new DateOnly(2026, 11, 2), null, []);

    private static WeeklyTourOutcome Outcome(int placed = 2, int notPlaced = 0) => new(
        1, ContainerId, "Tour 1", 1,
        [new WeeklyTourDayOutcome(1, 2, placed, 5, false, [], Enumerable.Repeat("Customer", notPlaced).ToList())],
        [OwnA, OwnB]);

    private void GivenStored(params Guid[] shiftIds) =>
        _mediator.Send(Arg.Any<GetContainerTemplatesQuery>(), Arg.Any<CancellationToken>()).Returns(
        [
            new ContainerTemplateResource
            {
                ContainerId = ContainerId,
                ContainerTemplateItems = shiftIds.Select(id => new ContainerTemplateItemResource { ShiftId = id }).ToList()
            }
        ]);

    [Test]
    public async Task AConsistentResult_HasNoMismatch()
    {
        GivenStored(OwnA, OwnB);

        var mismatches = await _verifier.FindMismatchesAsync(Plan(), [Outcome()], CancellationToken.None);

        mismatches.ShouldBeEmpty();
    }

    [Test]
    public async Task AServiceOfAnotherTour_IsListed()
    {
        GivenStored(OwnA, Foreign);

        var mismatches = await _verifier.FindMismatchesAsync(Plan(), [Outcome()], CancellationToken.None);

        mismatches.ShouldContain(m => m.Contains("belongs to another tour"));
    }

    [Test]
    public async Task ATemplateMissingAPlacedService_IsListed()
    {
        GivenStored(OwnA);

        var mismatches = await _verifier.FindMismatchesAsync(Plan(), [Outcome()], CancellationToken.None);

        mismatches.ShouldContain(m => m.Contains("do not hold exactly the services reported as placed"));
    }

    [Test]
    public async Task AServiceStoredTwice_IsListed()
    {
        GivenStored(OwnA, OwnA);

        var mismatches = await _verifier.FindMismatchesAsync(Plan(), [Outcome()], CancellationToken.None);

        mismatches.ShouldNotBeEmpty();
    }

    [Test]
    public async Task AWeekdayWhosePlacedAndNotPlacedDoNotAddUp_IsListed()
    {
        GivenStored(OwnA, OwnB);

        var mismatches = await _verifier.FindMismatchesAsync(Plan(), [Outcome(placed: 1, notPlaced: 0)], CancellationToken.None);

        mismatches.ShouldContain(m => m.Contains("do not add up"));
    }

    [Test]
    public async Task ANotPlacedServiceThatIsReported_AddsUp()
    {
        GivenStored(OwnA);
        var outcome = Outcome(placed: 1, notPlaced: 1) with { PlacedShiftIds = [OwnA] };

        var mismatches = await _verifier.FindMismatchesAsync(Plan(), [outcome], CancellationToken.None);

        mismatches.ShouldBeEmpty();
    }
}
