// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for BulkWorkDeletionPlanner: the sentence's parameters resolve to the exact Works of the main plan
/// (no scenario rows, no container children), and the plan is refused - never truncated - when the employee is
/// unknown or hidden, nothing matches, more than the per-call cap match, a Work is locked or a day is sealed.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.UnitTest.Skills;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class BulkWorkDeletionPlannerTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid EarlyShiftId = Guid.NewGuid();
    private static readonly Guid LateShiftId = Guid.NewGuid();
    private static readonly DateOnly FirstDay = new(2026, 11, 2);

    private IWorkRepository _workRepository = null!;
    private IClientRepository _clientRepository = null!;
    private IClientSearchRepository _searchRepository = null!;
    private IClientVisibilityGuard _visibility = null!;
    private ISealedDayRepository _sealedDays = null!;
    private IShiftRepository _shiftRepository = null!;
    private ICompanyClock _clock = null!;

    [SetUp]
    public void SetUp()
    {
        _workRepository = Substitute.For<IWorkRepository>();
        _clientRepository = Substitute.For<IClientRepository>();
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _visibility = SkillClientVisibility.AllVisible();
        _sealedDays = Substitute.For<ISealedDayRepository>();
        _shiftRepository = Substitute.For<IShiftRepository>();
        _clock = Substitute.For<ICompanyClock>();

        _clock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(new DateTime(2026, 10, 13, 0, 0, 0, DateTimeKind.Utc));
        _clientRepository.Get(ClientId).Returns(new Client { Id = ClientId, FirstName = "Max", Name = "Müller" });
        _clientRepository.Exists(ClientId).Returns(true);
        _searchRepository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                Items = [new ClientSearchItem { Id = ClientId, FirstName = "Max", LastName = "Müller", IdNumber = 11 }],
                TotalCount = 1
            });
        _sealedDays.GetLockedPairsAsync(
                Arg.Any<IReadOnlyCollection<(DateOnly Date, Guid ClientId)>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<(DateOnly Date, Guid ClientId)>());
        _shiftRepository.Get(EarlyShiftId).Returns(new Shift { Id = EarlyShiftId, Name = "Early", Abbreviation = "E" });
        _shiftRepository.Get(LateShiftId).Returns(new Shift { Id = LateShiftId, Name = "Late", Abbreviation = "L" });
        GivenWorks(NewWork(FirstDay, EarlyShiftId), NewWork(FirstDay.AddDays(1), LateShiftId));
    }

    private BulkWorkDeletionPlanner NewPlanner() => new(
        _workRepository, _clientRepository, _searchRepository, _visibility, _sealedDays, _shiftRepository, _clock);

    private static Work NewWork(DateOnly date, Guid shiftId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = ClientId,
        ShiftId = shiftId,
        CurrentDate = date,
        StartTime = new TimeOnly(6, 0),
        EndTime = new TimeOnly(14, 0)
    };

    private void GivenWorks(params Work[] works)
    {
        _workRepository.GetByClientAndDateRangeAsync(
                ClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(works.ToList());
    }

    private static Dictionary<string, object> ByName() => new()
    {
        ["firstName"] = "Max",
        ["lastName"] = "Müller",
        ["fromDate"] = "2026-11-01",
        ["untilDate"] = "2026-11-30"
    };

    [Test]
    public async Task ByName_PlansTheMainPlanParents_WithShiftNames()
    {
        var child = NewWork(FirstDay, EarlyShiftId);
        child.ParentWorkId = Guid.NewGuid();
        var scenarioWork = NewWork(FirstDay, EarlyShiftId);
        scenarioWork.AnalyseToken = Guid.NewGuid();
        var first = NewWork(FirstDay, EarlyShiftId);
        var second = NewWork(FirstDay.AddDays(1), LateShiftId);
        GivenWorks(second, child, scenarioWork, first);

        var plan = await NewPlanner().PlanAsync(ByName(), null);

        plan.IsRefused.ShouldBeFalse(plan.Refusal);
        plan.Works.Select(w => w.WorkId).ShouldBe([first.Id, second.Id]);
        plan.Works[0].ShiftName.ShouldBe("Early");
        plan.EmployeeName.ShouldBe("Max Müller");
        plan.From.ShouldBe(new DateOnly(2026, 11, 1));
        plan.Until.ShouldBe(new DateOnly(2026, 11, 30));
    }

    [Test]
    public async Task ByClientId_PlansTheSameWorks()
    {
        var parameters = new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["fromDate"] = "2026-11-01",
            ["untilDate"] = "2026-11-30"
        };

        var plan = await NewPlanner().PlanAsync(parameters, null);

        plan.IsRefused.ShouldBeFalse(plan.Refusal);
        plan.Works.Count.ShouldBe(2);
    }

    [Test]
    public async Task UntilDateOmitted_MeansTheSingleDay()
    {
        var parameters = ByName();
        parameters.Remove("untilDate");

        var plan = await NewPlanner().PlanAsync(parameters, null);

        plan.Until.ShouldBe(plan.From);
    }

    [Test]
    public async Task ShiftFilter_KeepsOnlyThatShift()
    {
        var parameters = ByName();
        parameters["shiftId"] = LateShiftId.ToString();

        var plan = await NewPlanner().PlanAsync(parameters, null);

        plan.Works.Select(w => w.ShiftId).ShouldAllBe(id => id == LateShiftId);
        plan.Works.Count.ShouldBe(1);
    }

    [Test]
    public async Task InvalidShiftId_IsRefused()
    {
        var parameters = ByName();
        parameters["shiftId"] = "not-a-uuid";

        (await NewPlanner().PlanAsync(parameters, null)).Refusal.ShouldContain("shiftId");
    }

    [Test]
    public async Task NoEmployee_IsRefused()
    {
        var plan = await NewPlanner().PlanAsync(
            new Dictionary<string, object> { ["fromDate"] = "2026-11-01" }, null);

        plan.IsRefused.ShouldBeTrue();
        plan.Refusal.ShouldContain("employee");
    }

    [Test]
    public async Task NoFromDate_IsRefused()
    {
        var parameters = ByName();
        parameters.Remove("fromDate");

        var plan = await NewPlanner().PlanAsync(parameters, null);

        plan.IsRefused.ShouldBeTrue();
        plan.Refusal.ShouldContain("fromDate");
    }

    [Test]
    public async Task InvalidDate_IsRefused()
    {
        var parameters = ByName();
        parameters["untilDate"] = "someday";

        var plan = await NewPlanner().PlanAsync(parameters, null);

        plan.IsRefused.ShouldBeTrue();
        plan.Refusal.ShouldContain("untilDate");
    }

    [Test]
    public async Task FromAfterUntil_IsRefused()
    {
        var parameters = ByName();
        parameters["fromDate"] = "2026-12-01";

        (await NewPlanner().PlanAsync(parameters, null)).IsRefused.ShouldBeTrue();
    }

    [Test]
    public async Task HiddenEmployee_AnswersLikeAnUnknownOne()
    {
        _visibility = SkillClientVisibility.Hiding(ClientId);
        var hiddenParameters = new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["fromDate"] = "2026-11-01"
        };
        var unknownId = Guid.NewGuid();
        var unknownParameters = new Dictionary<string, object>
        {
            ["clientId"] = unknownId.ToString(),
            ["fromDate"] = "2026-11-01"
        };

        var hidden = await NewPlanner().PlanAsync(hiddenParameters, null);
        var unknown = await NewPlanner().PlanAsync(unknownParameters, null);

        hidden.IsRefused.ShouldBeTrue();
        SkillClientVisibility.WithoutId(hidden.Refusal, ClientId)
            .ShouldBe(SkillClientVisibility.WithoutId(unknown.Refusal, unknownId));
    }

    [Test]
    public async Task NothingMatches_IsRefused()
    {
        GivenWorks();

        var plan = await NewPlanner().PlanAsync(ByName(), null);

        plan.IsRefused.ShouldBeTrue();
        plan.Refusal.ShouldContain("no work");
    }

    [Test]
    public async Task MoreThanTheCap_IsRefusedAsAWhole_NeverTruncated()
    {
        GivenWorks(Enumerable.Range(0, BulkWorkDeletionDefaults.MaxWorksPerCall + 1)
            .Select(i => NewWork(FirstDay.AddDays(i), EarlyShiftId)).ToArray());

        var plan = await NewPlanner().PlanAsync(ByName(), null);

        plan.IsRefused.ShouldBeTrue();
        plan.Works.ShouldBeEmpty();
        plan.Refusal.ShouldContain((BulkWorkDeletionDefaults.MaxWorksPerCall + 1).ToString());
        plan.Refusal.ShouldContain(BulkWorkDeletionDefaults.MaxWorksPerCall.ToString());
    }

    [Test]
    public async Task ExactlyTheCap_IsAllowed()
    {
        GivenWorks(Enumerable.Range(0, BulkWorkDeletionDefaults.MaxWorksPerCall)
            .Select(i => NewWork(FirstDay.AddDays(i), EarlyShiftId)).ToArray());

        var plan = await NewPlanner().PlanAsync(ByName(), null);

        plan.IsRefused.ShouldBeFalse(plan.Refusal);
        plan.Works.Count.ShouldBe(BulkWorkDeletionDefaults.MaxWorksPerCall);
    }

    [TestCase(WorkLockLevel.Confirmed)]
    [TestCase(WorkLockLevel.Approved)]
    [TestCase(WorkLockLevel.Closed)]
    public async Task OneLockedWork_RefusesTheWholePlan(WorkLockLevel level)
    {
        var locked = NewWork(FirstDay, EarlyShiftId);
        locked.LockLevel = level;
        GivenWorks(NewWork(FirstDay.AddDays(1), LateShiftId), locked);

        var plan = await NewPlanner().PlanAsync(ByName(), null);

        plan.IsRefused.ShouldBeTrue();
        plan.Works.ShouldBeEmpty();
        plan.Refusal.ShouldContain(level.ToString());
        plan.Refusal.ShouldContain(locked.Id.ToString());
    }

    [Test]
    public async Task OneSealedDay_RefusesTheWholePlan()
    {
        _sealedDays.GetLockedPairsAsync(
                Arg.Any<IReadOnlyCollection<(DateOnly Date, Guid ClientId)>>(), Arg.Any<CancellationToken>())
            .Returns(new HashSet<(DateOnly Date, Guid ClientId)> { (FirstDay.AddDays(1), ClientId) });

        var plan = await NewPlanner().PlanAsync(ByName(), null);

        plan.IsRefused.ShouldBeTrue();
        plan.Works.ShouldBeEmpty();
        plan.Refusal.ShouldContain("sealed");
        plan.Refusal.ShouldContain("2026-11-03");
    }
}
