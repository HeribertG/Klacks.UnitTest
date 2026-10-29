// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Services.RouteOptimization;
using Klacks.Api.Domain.Services.RouteOptimization.VisitScheduling;

namespace Klacks.UnitTest.Domain.Services.RouteOptimization.VisitScheduling;

/// <summary>
/// RouteStepBuilder keeps the two leg-time rules of the route endpoints: the route optimization takes the routing
/// profile of the next stop's mode in mix mode, the autofill always the plain duration matrix; the last stop has no leg.
/// </summary>
[TestFixture]
public class RouteStepBuilderTests
{
    private static readonly Guid ShiftA = Guid.NewGuid();
    private static readonly Guid ShiftB = Guid.NewGuid();

    [Test]
    public void MixMode_TakesTheProfileOfTheNextStopsMode()
    {
        // Arrange
        var result = Optimization(ContainerTransportMode.Mix, new Dictionary<string, double[,]>
        {
            ["foot"] = Matrix(900),
            ["driving"] = Matrix(300)
        });

        // Act
        var steps = RouteStepBuilder.FromOptimization(result);

        // Assert
        steps.Count.ShouldBe(2);
        steps[0].TravelTimeToNext.ShouldBe(TimeSpan.FromSeconds(900));
        steps[0].DistanceToNextKm.ShouldBe(2.5);
        steps[0].Order.ShouldBe(1);
        steps[1].TravelTimeToNext.ShouldBe(TimeSpan.Zero);
        steps[1].ShiftId.ShouldBe(ShiftB);
    }

    [Test]
    public void SingleMode_TakesTheDurationMatrix()
    {
        // Arrange
        var result = Optimization(ContainerTransportMode.ByCar, new Dictionary<string, double[,]> { ["foot"] = Matrix(900) });

        // Act
        var steps = RouteStepBuilder.FromOptimization(result);

        // Assert
        steps[0].TravelTimeToNext.ShouldBe(TimeSpan.FromSeconds(120));
    }

    [Test]
    public void Autofill_TakesTheDurationMatrixEvenInMixMode()
    {
        // Arrange
        var autofill = new ContainerAutofillResult(
            Route(), [ShiftA, ShiftB], 2.5, TimeSpan.Zero, TimeSpan.Zero, TimeSpan.Zero, 2, 2,
            Matrix(2.5), Matrix(120), TimeSpan.Zero, [0, 1], 0, 0, TimeSpan.Zero, [0, 1],
            new Dictionary<string, double[,]> { ["foot"] = Matrix(900) }, ContainerTransportMode.Mix);

        // Act
        var steps = RouteStepBuilder.FromAutofill(autofill);

        // Assert
        steps[0].TravelTimeToNext.ShouldBe(TimeSpan.FromSeconds(120));
    }

    private static RouteOptimizationResult Optimization(
        ContainerTransportMode mode, Dictionary<string, double[,]> byProfile) =>
        new(Route(), 2.5, TimeSpan.Zero, Matrix(2.5), Matrix(120), TimeSpan.Zero, [0, 1], 0, 0, TimeSpan.Zero, [0, 1],
            byProfile, mode);

    private static List<Location> Route() =>
    [
        new() { Name = "A", ShiftId = ShiftA, TransportMode = TransportMode.ByCar },
        new() { Name = "B", ShiftId = ShiftB, TransportMode = TransportMode.ByFoot }
    ];

    private static double[,] Matrix(double forwardValue) => new double[,] { { 0, forwardValue }, { forwardValue, 0 } };
}
