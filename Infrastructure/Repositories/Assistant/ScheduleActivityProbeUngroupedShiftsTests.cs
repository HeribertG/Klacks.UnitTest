// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ScheduleActivityProbe.CountUngroupedPlannableShiftsAsync against EF InMemory: they pin
/// the PREDICATE - which duty counts as ungrouped and plannable - not the SQL. Whether Npgsql translates
/// the negated membership subquery at all is a different claim that only the real database can make, and
/// UngroupedShiftsProbeLiveScanTests in Klacks.IntegrationTest makes it.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Assistant;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Repositories.Assistant;

[TestFixture]
public class ScheduleActivityProbeUngroupedShiftsTests
{
    private static readonly DateOnly Today = new(2026, 9, 21);

    private DbContextOptions<DataBaseContext> _options = null!;
    private IHttpContextAccessor _httpAccessor = null!;

    [SetUp]
    public void SetUp()
    {
        _options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _httpAccessor = Substitute.For<IHttpContextAccessor>();
    }

    private DataBaseContext CreateContext() => new(_options, _httpAccessor);

    private ScheduleActivityProbe CreateProbe() => new(CreateContext());

    private static Shift PlannableTask() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Duty",
        Status = ShiftStatus.OriginalShift,
        ShiftType = ShiftType.IsTask,
        FromDate = Today.AddDays(-30),
        UntilDate = null
    };

    private async Task GivenAsync(params Shift[] shifts)
    {
        await using var context = CreateContext();
        await context.Shift.AddRangeAsync(shifts);
        await context.SaveChangesAsync();
    }

    private async Task GivenMembershipAsync(Guid shiftId, bool isDeleted = false, Guid? analyseToken = null)
    {
        await using var context = CreateContext();
        await context.GroupItem.AddAsync(new GroupItem
        {
            Id = Guid.NewGuid(),
            ShiftId = shiftId,
            GroupId = Guid.NewGuid(),
            IsDeleted = isDeleted,
            AnalyseToken = analyseToken
        });
        await context.SaveChangesAsync();
    }

    [Test]
    public async Task CountUngroupedPlannableShiftsAsync_OriginalAndSplitWithoutAGroup_AreBothCounted()
    {
        var original = PlannableTask();
        var split = PlannableTask();
        split.Status = ShiftStatus.SplitShift;

        await GivenAsync(original, split);

        (await CreateProbe().CountUngroupedPlannableShiftsAsync(Today)).ShouldBe(2);
    }

    [Test]
    public async Task CountUngroupedPlannableShiftsAsync_ADutyWithALiveMembership_IsNotCounted()
    {
        var grouped = PlannableTask();
        await GivenAsync(grouped);
        await GivenMembershipAsync(grouped.Id);

        (await CreateProbe().CountUngroupedPlannableShiftsAsync(Today)).ShouldBe(0);
    }

    /// <summary>
    /// A soft-deleted membership is a membership that was taken away, and a scenario membership belongs
    /// to a what-if clone - neither makes the real duty grouped.
    /// </summary>
    [Test]
    public async Task CountUngroupedPlannableShiftsAsync_DeletedOrScenarioMembership_StillCountsAsUngrouped()
    {
        var removedMembership = PlannableTask();
        var scenarioMembership = PlannableTask();
        await GivenAsync(removedMembership, scenarioMembership);
        await GivenMembershipAsync(removedMembership.Id, isDeleted: true);
        await GivenMembershipAsync(scenarioMembership.Id, analyseToken: Guid.NewGuid());

        (await CreateProbe().CountUngroupedPlannableShiftsAsync(Today)).ShouldBe(2);
    }

    [Test]
    public async Task CountUngroupedPlannableShiftsAsync_ContainerOrderDeletedScenarioAndExpired_AreAllExcluded()
    {
        var container = PlannableTask();
        container.ShiftType = ShiftType.IsContainer;

        var order = PlannableTask();
        order.Status = ShiftStatus.OriginalOrder;

        var deleted = PlannableTask();
        deleted.IsDeleted = true;

        var scenarioClone = PlannableTask();
        scenarioClone.AnalyseToken = Guid.NewGuid();

        var scenarioSourced = PlannableTask();
        scenarioSourced.ScenarioSourceShiftId = Guid.NewGuid();

        var expired = PlannableTask();
        expired.UntilDate = Today.AddDays(-1);

        await GivenAsync(container, order, deleted, scenarioClone, scenarioSourced, expired);

        (await CreateProbe().CountUngroupedPlannableShiftsAsync(Today)).ShouldBe(0);
    }

    /// <summary>
    /// The window is inclusive on its last day and open towards the future: a duty valid up to today is
    /// still being planned, and one that only starts next year is planned too.
    /// </summary>
    [Test]
    public async Task CountUngroupedPlannableShiftsAsync_EndsTodayOrStartsInTheFuture_AreBothCounted()
    {
        var endsToday = PlannableTask();
        endsToday.UntilDate = Today;

        var startsLater = PlannableTask();
        startsLater.FromDate = Today.AddYears(1);

        await GivenAsync(endsToday, startsLater);

        (await CreateProbe().CountUngroupedPlannableShiftsAsync(Today)).ShouldBe(2);
    }
}
