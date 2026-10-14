// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the get_replacement_status skill: it needs a visible absent employee, reads the request book through
/// ListReplacementRequestsQuery (default period 7 days back to 14 ahead), groups the rows per scenario x date x shift with the
/// derived coverage status, labels a merely proposed candidate as not asked, hides the name of a shift outside the caller's
/// group visibility, says plainly when nothing is recorded, and shows only the escalation chains the visibility filter keeps.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Escalation;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetReplacementStatusSkillTests
{
    private static readonly Guid AbsentId = Guid.NewGuid();
    private static readonly Guid CandidateA = Guid.NewGuid();
    private static readonly Guid CandidateB = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();
    private static readonly DateOnly Today = new(2026, 3, 10);

    private IMediator _mediator = null!;
    private IClientSearchRepository _search = null!;
    private IClientRepository _clients = null!;
    private IClientVisibilityGuard _clientGuard = null!;
    private IShiftRepository _shifts = null!;
    private IGroupVisibilityGuard _groupGuard = null!;
    private IAbsenceRepository _absences = null!;
    private IEscalationChainRepository _chains = null!;
    private IEscalationChainVisibilityFilter _chainFilter = null!;
    private ICompanyClock _clock = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _search = Substitute.For<IClientSearchRepository>();
        _clients = Substitute.For<IClientRepository>();
        _clientGuard = Substitute.For<IClientVisibilityGuard>();
        _clientGuard.IsVisibleAsync(AbsentId, Arg.Any<CancellationToken>()).Returns(true);
        _clients.Get(AbsentId).Returns(new Client { Id = AbsentId, FirstName = "Max", Name = "Mueller" });
        _clients.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>()).Returns(new List<Client>
        {
            new() { Id = CandidateA, FirstName = "Anna", Name = "Alpha" },
            new() { Id = CandidateB, FirstName = "Ben", Name = "Beta" },
        });
        _shifts = Substitute.For<IShiftRepository>();
        _shifts.Get(ShiftId).Returns(new Shift { Id = ShiftId, Name = "Early", GroupItems = new List<GroupItem>() });
        _groupGuard = Substitute.For<IGroupVisibilityGuard>();
        _groupGuard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(true);
        _absences = Substitute.For<IAbsenceRepository>();
        _chains = Substitute.For<IEscalationChainRepository>();
        _chains.GetAbsenceCoverageChainsByAbsentClientAsync(
                AbsentId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<EscalationChain>());
        _chainFilter = Substitute.For<IEscalationChainVisibilityFilter>();
        _chainFilter.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<EscalationChain>>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<IReadOnlyCollection<EscalationChain>>().ToList()));
        _clock = Substitute.For<ICompanyClock>();
        _clock.GetTodayDateAsync(Arg.Any<CancellationToken>()).Returns(Today);
        _clock.GetTimeZoneAsync(Arg.Any<CancellationToken>()).Returns(TimeZoneInfo.Utc);
        GivenRows();
    }

    private void GivenRows(params ReplacementRequestResource[] rows)
    {
        _mediator.Send(Arg.Any<ListReplacementRequestsQuery>(), Arg.Any<CancellationToken>())
            .Returns(rows.ToList());
    }

    private static ReplacementRequestResource Row(
        Guid candidate, ReplacementRequestOutcome outcome, DateOnly? date = null, Guid? token = null, bool applied = false) => new(
        Guid.NewGuid(), AbsentId, candidate, ShiftId, date ?? Today, new TimeOnly(6, 0), new TimeOnly(14, 0), null, null,
        ReplacementRequestSource.PlannerDialog, outcome, Today.ToDateTime(TimeOnly.MinValue), Today.ToDateTime(TimeOnly.MinValue),
        null, null, token, null, applied ? Today.ToDateTime(TimeOnly.MinValue) : null, false);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "planner",
        UserPermissions = new List<string> { "CanViewSchedule" }
    };

    private GetReplacementStatusSkill Skill() => new(
        _mediator, _search, _clients, _clientGuard, _shifts, _groupGuard, _absences, _chains, _chainFilter, _clock);

    private static Dictionary<string, object> ById() => new() { ["clientId"] = AbsentId.ToString() };

    private static JsonElement Json(SkillResult result) => JsonSerializer.SerializeToElement(result.Data);

    [Test]
    public async Task NoClientGiven_AsksWhoIsAbsent()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        Assert.That(result.Success, Is.False);
        await _mediator.DidNotReceive().Send(Arg.Any<ListReplacementRequestsQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HiddenOrUnknownClient_AnswersNotFound_WithoutReadingTheBook()
    {
        var hidden = Guid.NewGuid();
        _clientGuard.IsVisibleAsync(hidden, Arg.Any<CancellationToken>()).Returns(false);

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["clientId"] = hidden.ToString() });

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not found"));
        await _mediator.DidNotReceive().Send(Arg.Any<ListReplacementRequestsQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DefaultPeriod_IsSevenDaysBackAndFourteenAhead()
    {
        await Skill().ExecuteAsync(Ctx(), ById());

        await _mediator.Received(1).Send(
            Arg.Is<ListReplacementRequestsQuery>(q =>
                q.AbsentClientId == AbsentId && q.FromDate == Today.AddDays(-7) && q.UntilDate == Today.AddDays(14)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UntilBeforeFrom_IsRefused()
    {
        var parameters = ById();
        parameters["fromDate"] = Today;
        parameters["untilDate"] = Today.AddDays(-1);

        var result = await Skill().ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public async Task NothingRecorded_SaysSoWithoutClaimingNobodyWasCalled()
    {
        var result = await Skill().ExecuteAsync(Ctx(), ById());

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("Nothing is recorded"));
        Assert.That(result.Message, Does.Contain("does NOT prove"));
        Assert.That(Json(result).GetProperty("SlotCount").GetInt32(), Is.EqualTo(0));
    }

    [Test]
    public async Task RowsOfOneSlot_AreGroupedWithDerivedStatusAndHonestContactLabels()
    {
        GivenRows(
            Row(CandidateA, ReplacementRequestOutcome.Declined),
            Row(CandidateB, ReplacementRequestOutcome.Requested),
            Row(Guid.NewGuid(), ReplacementRequestOutcome.Proposed));

        var result = await Skill().ExecuteAsync(Ctx(), ById());

        var slots = Json(result).GetProperty("Slots");
        Assert.That(slots.GetArrayLength(), Is.EqualTo(1));
        var slot = slots[0];
        Assert.That(slot.GetProperty("Status").GetString(), Is.EqualTo(nameof(ReplacementSlotStatus.AwaitingAnswer)));
        Assert.That(slot.GetProperty("ShiftName").GetString(), Is.EqualTo("Early"));
        var contacts = slot.GetProperty("Candidates").EnumerateArray().Select(c => c.GetProperty("Contact").GetString()).ToList();
        Assert.That(contacts, Is.EquivalentTo(new[] { "Declined", "AskedNoAnswerYet", "ProposedNotAsked" }));
    }

    [Test]
    public async Task AcceptedCandidate_MakesTheSlotCovered()
    {
        GivenRows(Row(CandidateA, ReplacementRequestOutcome.Accepted));

        var result = await Skill().ExecuteAsync(Ctx(), ById());

        Assert.That(Json(result).GetProperty("Slots")[0].GetProperty("Status").GetString(), Is.EqualTo(nameof(ReplacementSlotStatus.Covered)));
        Assert.That(result.Message, Does.Contain("1 covered"));
    }

    [Test]
    public async Task ScenarioRowsAndRealPlanRows_FormSeparateSlots()
    {
        GivenRows(
            Row(CandidateA, ReplacementRequestOutcome.Accepted, token: Guid.NewGuid()),
            Row(CandidateB, ReplacementRequestOutcome.Declined));

        var result = await Skill().ExecuteAsync(Ctx(), ById());

        var slots = Json(result).GetProperty("Slots");
        Assert.That(slots.GetArrayLength(), Is.EqualTo(2));
        Assert.That(slots.EnumerateArray().Count(s => s.GetProperty("IsScenario").GetBoolean()), Is.EqualTo(1));
    }

    [Test]
    public async Task ShiftOutsideGroupVisibility_IsListedWithoutItsName()
    {
        var hiddenGroup = Guid.NewGuid();
        _shifts.Get(ShiftId).Returns(new Shift
        {
            Id = ShiftId,
            Name = "Secret shift",
            GroupItems = new List<GroupItem> { new() { GroupId = hiddenGroup } }
        });
        _groupGuard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(false);
        _groupGuard.IsGroupVisibleAsync(hiddenGroup, Arg.Any<CancellationToken>()).Returns(false);
        GivenRows(Row(CandidateA, ReplacementRequestOutcome.Requested));

        var result = await Skill().ExecuteAsync(Ctx(), ById());

        Assert.That(Json(result).GetProperty("Slots")[0].GetProperty("ShiftName").ValueKind, Is.EqualTo(JsonValueKind.Null));
        Assert.That(JsonSerializer.Serialize(result.Data), Does.Not.Contain("Secret shift"));
    }

    [Test]
    public async Task EscalationChains_AreRunThroughTheVisibilityFilterWithTheCallerId()
    {
        var context = Ctx();
        var shown = new EscalationChain
        {
            Id = Guid.NewGuid(),
            Status = EscalationChainStatus.Acknowledged,
            AcknowledgedByUserName = "Planner B",
            Stages = new List<EscalationStage>
            {
                new() { Rank = 1, UserDisplayName = "Planner A", Status = EscalationStageStatus.Expired },
                new() { Rank = 2, UserDisplayName = "Planner B", Status = EscalationStageStatus.Acknowledged },
            }
        };
        var hidden = new EscalationChain { Id = Guid.NewGuid() };
        _chains.GetAbsenceCoverageChainsByAbsentClientAsync(
                AbsentId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<EscalationChain> { shown, hidden });
        _chainFilter.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<EscalationChain>>(), context.UserId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new List<EscalationChain> { shown });

        var result = await Skill().ExecuteAsync(context, ById());

        var chains = Json(result).GetProperty("EscalationChains");
        Assert.That(chains.GetArrayLength(), Is.EqualTo(1));
        Assert.That(chains[0].GetProperty("TakenOverBy").GetString(), Is.EqualTo("Planner B"));
        Assert.That(chains[0].GetProperty("WokenPlanners").GetArrayLength(), Is.EqualTo(2));
    }
}
