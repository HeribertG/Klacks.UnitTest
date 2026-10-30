// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// evaluate_container_bundling over the real autofill and available-task list (InMemoryContainerAutofill): a group keeps
/// its filter as in the bundling even with one task, a hidden service appears nowhere, an unreachable routing service gives
/// no fit statement, the validity starts today in the company zone when none is asked for, and nothing is added to the
/// database.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class EvaluateContainerBundlingSkillTests
{
    private const int IsoWednesday = 3;
    private const string WinterthurGroupName = "Spitex Winterthur";
    private const string ZurichClient = "Zurich";

    private static readonly DateOnly TaskStart = new(2026, 11, 2);
    private static readonly DateTime CompanyToday = new(2026, 11, 4, 0, 0, 0, DateTimeKind.Utc);

    private DataBaseContext _context = null!;
    private ServiceProvider? _provider;
    private Shift _winterthurTask = null!;
    private Shift _zurichTask = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var winterthurGroup = new Group { Id = Guid.NewGuid(), Name = WinterthurGroupName };
        var zurichGroup = new Group { Id = Guid.NewGuid(), Name = "Spitex Zürich" };
        _winterthurTask = TaskShift("Winterthur", 1, winterthurGroup.Id);
        _zurichTask = TaskShift(ZurichClient, 2, zurichGroup.Id);
        await _context.Group.AddRangeAsync(winterthurGroup, zurichGroup);
        await _context.Shift.AddRangeAsync(_winterthurTask, _zurichTask);
        await _context.SaveChangesAsync();
    }

    [TearDown]
    public void TearDown()
    {
        _provider?.Dispose();
        _context.Dispose();
    }

    [Test]
    public async Task BothServicesFit_AreReportedWithTheirStopsAndTravelTime()
    {
        var evaluation = await EvaluateAsync(Params());

        evaluation.Verdict.ShouldBe(ContainerBundlingVerdict.AllFit);
        evaluation.Route.ShouldNotBeNull();
        evaluation.Route.Stops.Select(stop => stop.ShiftId).ShouldBe([_winterthurTask.Id, _zurichTask.Id], ignoreOrder: true);
        evaluation.Route.TravelMinutes.ShouldBe(40);
        evaluation.Route.OnSiteMinutes.ShouldBe(90);
    }

    [Test]
    public async Task GroupWithOneService_KeepsItsFilterLikeTheBundling()
    {
        var evaluation = await EvaluateAsync(Params(WinterthurGroupName));

        evaluation.CandidateCount.ShouldBe(1);
        evaluation.Verdict.ShouldBe(ContainerBundlingVerdict.SingleVisitOnly);
        evaluation.Route!.Stops.ShouldHaveSingleItem().ShiftId.ShouldBe(_winterthurTask.Id);
    }

    [Test]
    public async Task HiddenService_AppearsNowhere()
    {
        var result = await Skill(ShiftVisibilityGuards.Hiding(_zurichTask.Id)).ExecuteAsync(Ctx(), Params());

        var evaluation = result.Data.ShouldBeOfType<ContainerBundlingEvaluation>();
        evaluation.CandidateCount.ShouldBe(1);
        var json = JsonSerializer.Serialize(evaluation);
        json.ShouldNotContain(ZurichClient);
        json.ShouldNotContain(_zurichTask.Id.ToString());
    }

    [Test]
    public async Task RoutingServiceDown_GivesNoFitStatement()
    {
        var evaluation = await EvaluateAsync(Params(), estimated: true);

        evaluation.Verdict.ShouldBe(ContainerBundlingVerdict.TravelTimesNotCalculated);
        evaluation.TravelTimes.ShouldBe(ContainerBundlingTravelTimes.NotCalculated);
        evaluation.Route.ShouldBeNull();
    }

    [Test]
    public async Task WithoutFromDate_TheValidityStartsTodayInTheCompanyZone()
    {
        _winterthurTask.UntilDate = DateOnly.FromDateTime(CompanyToday).AddDays(-1);
        _context.Update(_winterthurTask);
        await _context.SaveChangesAsync();
        var parameters = Params();
        parameters.Remove("fromDate");

        var evaluation = await EvaluateAsync(parameters);

        evaluation.CandidateCount.ShouldBe(1);
    }

    [TestCase(0)]
    [TestCase(1440)]
    public async Task BreakOutsideADay_IsRefused(int breakMinutes)
    {
        var parameters = Params();
        parameters["breakMinutes"] = breakMinutes;

        var result = await Skill(ShiftVisibilityGuards.AllowAll()).ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task Evaluation_AddsNothingToTheDatabase()
    {
        var shiftsBefore = await _context.Shift.CountAsync();

        await EvaluateAsync(Params());

        (await _context.Shift.CountAsync()).ShouldBe(shiftsBefore);
        (await _context.ContainerTemplate.CountAsync()).ShouldBe(0);
    }

    private async Task<ContainerBundlingEvaluation> EvaluateAsync(Dictionary<string, object> parameters, bool estimated = false)
    {
        var result = await Skill(ShiftVisibilityGuards.AllowAll(), estimated).ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue(result.Message);
        return result.Data.ShouldBeOfType<ContainerBundlingEvaluation>();
    }

    private EvaluateContainerBundlingSkill Skill(IShiftVisibilityGuard visibilityGuard, bool estimated = false)
    {
        var availableTasks = InMemoryContainerAutofill.AvailableTasks(_context);
        var (autofill, provider) = InMemoryContainerAutofill.Create(availableTasks, estimated: estimated);
        _provider = provider;

        var groupRepository = Substitute.For<IGroupRepository>();
        groupRepository.List().Returns(_ => _context.Group.AsNoTracking().ToList());

        var companyClock = Substitute.For<ICompanyClock>();
        companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(CompanyToday);

        return new EvaluateContainerBundlingSkill(
            availableTasks,
            autofill,
            visibilityGuard,
            groupRepository,
            TestGroupWriteVisibility.UnrestrictedGroups(),
            new GroupItemRepository(_context, Substitute.For<ILogger<GroupItem>>()),
            companyClock,
            Substitute.For<ISettingsReader>(),
            Substitute.For<ISettingsEncryptionService>(),
            GeoServicesPolicyTestFactory.Default());
    }

    private static Shift TaskShift(string name, double position, Guid groupId)
    {
        var id = Guid.NewGuid();
        return new Shift
        {
            Id = id,
            Name = name,
            Abbreviation = name,
            Description = name,
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = TaskStart,
            StartShift = new TimeOnly(8, 0),
            EndShift = new TimeOnly(17, 0),
            AfterShift = new TimeOnly(0, 0),
            BeforeShift = new TimeOnly(0, 0),
            WorkTime = 0.75m,
            IsTimeRange = true,
            IsWednesday = true,
            Client = InMemoryContainerAutofill.ClientAt(name, position),
            GroupItems = [new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = id }]
        };
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    private static Dictionary<string, object> Params(string? groupName = null)
    {
        var parameters = new Dictionary<string, object>
        {
            ["location"] = InMemoryContainerAutofill.BaseAddress,
            ["weekday"] = IsoWednesday,
            ["fromTime"] = "08:00",
            ["untilTime"] = "18:00",
            ["fromDate"] = "2026-11-01",
        };
        if (groupName != null)
        {
            parameters["groupName"] = groupName;
        }

        return parameters;
    }
}
