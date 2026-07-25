// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for IndividualPeriodBoundaryResolver: which Period covers a date, how open ended periods
/// (UntilDate == null) are bounded, and which misconfigurations are rejected instead of guessed.
/// </summary>

namespace Klacks.UnitTest.Domain.Services.Schedules;

using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Schedules;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class IndividualPeriodBoundaryResolverTests
{
    private const string PeriodName = "Payroll 2026";

    [Test]
    public void Resolve_DateInsideClosedPeriod_ReturnsThatPeriodsBoundaries()
    {
        // Arrange
        var periods = new List<Period>
        {
            CreatePeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 28)),
            CreatePeriod(new DateOnly(2026, 1, 29), new DateOnly(2026, 2, 25))
        };

        // Act
        var (start, end) = IndividualPeriodBoundaryResolver.Resolve(periods, new DateOnly(2026, 2, 10), PeriodName);

        // Assert
        start.ShouldBe(new DateOnly(2026, 1, 29));
        end.ShouldBe(new DateOnly(2026, 2, 25));
    }

    [Test]
    public void Resolve_DateOnPeriodBoundaries_IsInclusiveOnBothEnds()
    {
        // Arrange
        var periods = new List<Period> { CreatePeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)) };

        // Act
        var onStart = IndividualPeriodBoundaryResolver.Resolve(periods, new DateOnly(2026, 3, 1), PeriodName);
        var onEnd = IndividualPeriodBoundaryResolver.Resolve(periods, new DateOnly(2026, 3, 31), PeriodName);

        // Assert
        onStart.StartDate.ShouldBe(new DateOnly(2026, 3, 1));
        onEnd.EndDate.ShouldBe(new DateOnly(2026, 3, 31));
    }

    [Test]
    public void Resolve_OpenEndedPeriodWithSuccessor_EndsDayBeforeSuccessorStarts()
    {
        // Arrange
        var periods = new List<Period>
        {
            CreatePeriod(new DateOnly(2026, 1, 1), untilDate: null),
            CreatePeriod(new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30))
        };

        // Act
        var (start, end) = IndividualPeriodBoundaryResolver.Resolve(periods, new DateOnly(2026, 2, 15), PeriodName);

        // Assert
        start.ShouldBe(new DateOnly(2026, 1, 1));
        end.ShouldBe(new DateOnly(2026, 3, 31));
    }

    [Test]
    public void Resolve_OpenEndedPeriodWithSeveralSuccessors_UsesTheEarliestSuccessor()
    {
        // Arrange
        var periods = new List<Period>
        {
            CreatePeriod(new DateOnly(2026, 1, 1), untilDate: null),
            CreatePeriod(new DateOnly(2026, 9, 1), new DateOnly(2026, 9, 30)),
            CreatePeriod(new DateOnly(2026, 4, 1), new DateOnly(2026, 6, 30))
        };

        // Act
        var (_, end) = IndividualPeriodBoundaryResolver.Resolve(periods, new DateOnly(2026, 2, 15), PeriodName);

        // Assert
        end.ShouldBe(new DateOnly(2026, 3, 31));
    }

    [Test]
    public void Resolve_OpenEndedPeriodWithoutSuccessor_Throws()
    {
        // Arrange
        var periods = new List<Period> { CreatePeriod(new DateOnly(2026, 1, 1), untilDate: null) };

        // Act
        Action act = () => IndividualPeriodBoundaryResolver.Resolve(periods, new DateOnly(2026, 5, 5), PeriodName);

        // Assert
        var ex = Should.Throw<InvalidRequestException>(act);
        ex.Message.ShouldContain("open period");
        ex.Message.ShouldContain(PeriodName);
    }

    [Test]
    public void Resolve_OverlappingPeriods_LatestFromDateWins()
    {
        // Arrange
        var periods = new List<Period>
        {
            CreatePeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 3, 31)),
            CreatePeriod(new DateOnly(2026, 2, 1), new DateOnly(2026, 4, 30))
        };

        // Act
        var (start, end) = IndividualPeriodBoundaryResolver.Resolve(periods, new DateOnly(2026, 2, 15), PeriodName);

        // Assert
        start.ShouldBe(new DateOnly(2026, 2, 1));
        end.ShouldBe(new DateOnly(2026, 4, 30));
    }

    [Test]
    public void Resolve_DateInGapBetweenPeriods_Throws()
    {
        // Arrange
        var periods = new List<Period>
        {
            CreatePeriod(new DateOnly(2026, 1, 1), new DateOnly(2026, 1, 31)),
            CreatePeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31))
        };

        // Act
        Action act = () => IndividualPeriodBoundaryResolver.Resolve(periods, new DateOnly(2026, 2, 15), PeriodName);

        // Assert
        Should.Throw<InvalidRequestException>(act).Message.ShouldContain("no period covering");
    }

    [Test]
    public void Resolve_DateBeforeFirstPeriod_Throws()
    {
        // Arrange
        var periods = new List<Period> { CreatePeriod(new DateOnly(2026, 3, 1), new DateOnly(2026, 3, 31)) };

        // Act
        Action act = () => IndividualPeriodBoundaryResolver.Resolve(periods, new DateOnly(2026, 1, 15), PeriodName);

        // Assert
        Should.Throw<InvalidRequestException>(act);
    }

    [Test]
    public void Resolve_NoPeriodsDefined_Throws()
    {
        // Arrange
        var periods = new List<Period>();

        // Act
        Action act = () => IndividualPeriodBoundaryResolver.Resolve(periods, new DateOnly(2026, 1, 15), PeriodName);

        // Assert
        Should.Throw<InvalidRequestException>(act).Message.ShouldContain("no periods defined");
    }

    private static Period CreatePeriod(DateOnly fromDate, DateOnly? untilDate) => new()
    {
        Id = Guid.NewGuid(),
        IndividualPeriodId = Guid.NewGuid(),
        FromDate = fromDate,
        UntilDate = untilDate
    };
}
