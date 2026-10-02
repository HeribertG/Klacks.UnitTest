// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the coverage slots of the dashboard statistics: a regular shift day demands Quantity x SumEmployees
/// employees, sporadic shift days carry no fixed daily demand, and overstaffing one shift day never hides the gap
/// of another.
/// </summary>

using Klacks.Api.Application.DTOs.Dashboard;
using Klacks.Api.Application.Handlers.Dashboard;
using Klacks.Api.Application.Queries.Dashboard;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Handlers.Dashboard;

[TestFixture]
public class GetShiftCoverageStatisticsQueryHandlerTests
{
    private static readonly Guid GroupId = Guid.NewGuid();
    private const string GroupName = "Team";

    private IShiftCoverageReadRepository _readRepository = null!;
    private IShiftScheduleService _scheduleService = null!;
    private IGroupVisibilityService _visibility = null!;

    [SetUp]
    public void SetUp()
    {
        _readRepository = Substitute.For<IShiftCoverageReadRepository>();
        _scheduleService = Substitute.For<IShiftScheduleService>();
        _visibility = Substitute.For<IGroupVisibilityService>();
        _visibility.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Unrestricted());
        _readRepository.GetActiveGroups(Arg.Any<CancellationToken>()).Returns([(GroupId, GroupName)]);
        _readRepository.GetWorkLockEntries(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns([]);
    }

    private void Given(params ShiftDayAssignment[] days)
    {
        _readRepository.GetShiftGroupAssignments(Arg.Any<CancellationToken>())
            .Returns(days.Select(day => ((Guid?)day.ShiftId, GroupId)).Distinct().ToList());
        _scheduleService
            .GetShiftScheduleQuery(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<List<DateOnly>?>(), Arg.Any<List<Guid>?>(),
                Arg.Any<bool>(), Arg.Any<Guid?>())
            .Returns(new TestAsyncEnumerable<ShiftDayAssignment>(days));
    }

    private async Task<ShiftCoverageStatisticsResource> HandleSingleAsync()
    {
        var sut = new GetShiftCoverageStatisticsQueryHandler(
            _readRepository,
            _scheduleService,
            _visibility,
            new FixedCompanyClock(DateTimeOffset.UtcNow),
            NullLogger<GetShiftCoverageStatisticsQueryHandler>.Instance);

        return (await sut.Handle(new GetShiftCoverageStatisticsQuery(), CancellationToken.None)).Single();
    }

    private static ShiftDayAssignment Day(int quantity, int sumEmployees, int engaged, bool isSporadic = false) => new()
    {
        ShiftId = Guid.NewGuid(),
        Date = new DateOnly(2026, 10, 5),
        Quantity = quantity,
        SumEmployees = sumEmployees,
        Engaged = engaged,
        IsSporadic = isSporadic,
    };

    [Test]
    public async Task TotalSlots_AreQuantityTimesSumEmployees()
    {
        Given(Day(quantity: 2, sumEmployees: 3, engaged: 4));

        var result = await HandleSingleAsync();

        result.TotalSlots.ShouldBe(6);
        result.CoveredSlots.ShouldBe(4);
    }

    [Test]
    public async Task SporadicShiftDays_AreNotCounted()
    {
        Given(
            Day(quantity: 1, sumEmployees: 1, engaged: 0),
            Day(quantity: 3, sumEmployees: 2, engaged: 1, isSporadic: true));

        var result = await HandleSingleAsync();

        result.TotalSlots.ShouldBe(1);
        result.CoveredSlots.ShouldBe(0);
    }

    [Test]
    public async Task OverstaffedShiftDay_DoesNotHideAnotherGap()
    {
        Given(
            Day(quantity: 1, sumEmployees: 1, engaged: 3),
            Day(quantity: 1, sumEmployees: 1, engaged: 0));

        var result = await HandleSingleAsync();

        result.TotalSlots.ShouldBe(2);
        result.CoveredSlots.ShouldBe(1);
    }
}
