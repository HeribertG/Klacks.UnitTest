// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Klacks.Api.Application.DTOs.Filter;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Shifts;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

/// <summary>
/// find_split_shift_candidates over the real ShiftRepository (EF InMemory): only sealed orders, plannable shifts and
/// split parts with a live link to the group; rows grouped by their order (OriginalId, else their own id); the
/// plannable shift of an order is the first in repository order (original id, from date, start time); parts sorted
/// by start time; split orders first, then by name. The name filter (ILike) cannot run on InMemory and is pinned by
/// the integration test ShiftReadShapesTests. Every answer says it counts group membership only and reports the orders
/// without any group and, through the shift list (mediator), the orders that lie in a place named like the group.
/// </summary>
[TestFixture]
public class FindSplitShiftCandidatesSkillTests
{
    private const string GroupName = "Biel/Bienne";

    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid OtherGroupId = Guid.NewGuid();

    private InMemoryShiftStore _store = null!;
    private IGroupSearchRepository _groupSearch = null!;
    private IMediator _mediator = null!;
    private FindSplitShiftCandidatesSkill _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new InMemoryShiftStore();
        _groupSearch = Substitute.For<IGroupSearchRepository>();
        _groupSearch.SearchAsync(Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new GroupSearchResult
            {
                Items = [new GroupSearchItem { Id = GroupId, Name = GroupName }],
                TotalCount = 1,
            });
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<GetTruncatedListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new TruncatedShiftResource { Shifts = new List<ShiftResource>(), MaxItems = 0 });
        _sut = new FindSplitShiftCandidatesSkill(_store.Reader, _groupSearch, _mediator);
    }

    [TearDown]
    public void TearDown() => _store.Dispose();

    [Test]
    public async Task ListsTheGroupsOrders_SplitFirst_WithTheFirstPlannableShiftAndThePartsByStartTime()
    {
        var alpha = Make("Alpha", ShiftStatus.SealedOrder, originalId: null, new DateOnly(2026, 6, 1), new TimeOnly(7, 0));
        var alphaLater = Make("Alpha plan late", ShiftStatus.OriginalShift, alpha.Id, new DateOnly(2026, 6, 9), new TimeOnly(7, 0));
        var alphaEarlier = Make("Alpha plan early", ShiftStatus.OriginalShift, alpha.Id, new DateOnly(2026, 6, 2), new TimeOnly(7, 0));
        var alphaNight = Make("Alpha night", ShiftStatus.SplitShift, alpha.Id, new DateOnly(2026, 6, 1), new TimeOnly(19, 0));
        var alphaDay = Make("Alpha day", ShiftStatus.SplitShift, alpha.Id, new DateOnly(2026, 6, 1), new TimeOnly(7, 0));
        var beta = Make("Beta", ShiftStatus.SealedOrder, originalId: null, new DateOnly(2026, 5, 1), new TimeOnly(8, 0));
        var betaPlan = Make("Beta plan", ShiftStatus.OriginalShift, beta.Id, new DateOnly(2026, 5, 1), new TimeOnly(8, 0));
        _store.Seed(alpha, alphaLater, alphaEarlier, alphaNight, alphaDay, beta, betaPlan);

        var orders = await OrdersAsync();

        orders.Select(o => o.GetProperty("Name").GetString()).ShouldBe(["Alpha", "Beta"]);
        var split = orders[0];
        split.GetProperty("SealedOrderId").GetGuid().ShouldBe(alpha.Id);
        split.GetProperty("OrderShiftId").GetGuid().ShouldBe(alphaEarlier.Id);
        split.GetProperty("AlreadySplit").GetBoolean().ShouldBeTrue();
        split.GetProperty("PartCount").GetInt32().ShouldBe(2);
        split.GetProperty("Parts").EnumerateArray().Select(p => p.GetProperty("Id").GetGuid())
            .ShouldBe([alphaDay.Id, alphaNight.Id]);
        orders[1].GetProperty("OrderShiftId").GetGuid().ShouldBe(betaPlan.Id);
        orders[1].GetProperty("AlreadySplit").GetBoolean().ShouldBeFalse();
    }

    [Test]
    public async Task IgnoresOtherStatuses_DeletedLinks_AndOtherGroups()
    {
        var kept = Make("Kept", ShiftStatus.SealedOrder, originalId: null, new DateOnly(2026, 6, 1), new TimeOnly(7, 0));
        var draft = Make("Draft", ShiftStatus.OriginalOrder, originalId: null, new DateOnly(2026, 6, 1), new TimeOnly(7, 0));
        var unlinked = Make("Unlinked", ShiftStatus.SealedOrder, originalId: null, new DateOnly(2026, 6, 1), new TimeOnly(7, 0));
        unlinked.GroupItems.Single().IsDeleted = true;
        var foreign = Make("Foreign", ShiftStatus.SealedOrder, originalId: null, new DateOnly(2026, 6, 1), new TimeOnly(7, 0), OtherGroupId);
        _store.Seed(kept, draft, unlinked, foreign);

        var orders = await OrdersAsync();

        orders.Select(o => o.GetProperty("Name").GetString()).ShouldBe(["Kept"]);
    }

    [Test]
    public async Task AnOrderWithoutSealedOrderInTheGroup_IsHeadedByItsPlannableShift()
    {
        var hiddenOrderId = Guid.NewGuid();
        var plan = Make("Plan only", ShiftStatus.OriginalShift, hiddenOrderId, new DateOnly(2026, 6, 1), new TimeOnly(6, 0));
        _store.Seed(plan);

        var order = (await OrdersAsync()).Single();

        order.GetProperty("SealedOrderId").GetGuid().ShouldBe(hiddenOrderId);
        order.GetProperty("OrderShiftId").GetGuid().ShouldBe(plan.Id);
        order.GetProperty("Name").GetString().ShouldBe("Plan only");
    }

    /// <summary>
    /// Pins a known gap, not the wanted behaviour: the read has no scenario filter, and ScenarioShiftCloner copies a
    /// group's shifts together with their group links, so a scenario copy of an order is listed as a second order.
    /// </summary>
    [Test]
    public async Task KnownGap_AScenarioCopyOfAnOrder_IsListedAsASecondOrder()
    {
        var real = Make("Alpha", ShiftStatus.SealedOrder, originalId: null, new DateOnly(2026, 6, 1), new TimeOnly(7, 0));
        var scenarioCopy = Make("Alpha", ShiftStatus.SealedOrder, originalId: null, new DateOnly(2026, 6, 1), new TimeOnly(7, 0));
        scenarioCopy.AnalyseToken = Guid.NewGuid();
        scenarioCopy.ScenarioSourceShiftId = real.Id;
        _store.Seed(real, scenarioCopy);

        var orders = await OrdersAsync();

        orders.Select(o => o.GetProperty("SealedOrderId").GetGuid()).ShouldBe([real.Id, scenarioCopy.Id], ignoreOrder: true);
    }

    [Test]
    public async Task EmptyGroup_SaysItCountsMembershipOnly_AndReportsTheUngroupedOrders()
    {
        var openOrder = Make("Open", ShiftStatus.OriginalOrder, null, new DateOnly(2026, 6, 1), new TimeOnly(7, 0), grouped: false);
        var sealedOrder = Make("Sealed", ShiftStatus.SealedOrder, null, new DateOnly(2026, 6, 1), new TimeOnly(7, 0), grouped: false);
        var otherOpen = Make("Open 2", ShiftStatus.OriginalOrder, null, new DateOnly(2026, 6, 2), new TimeOnly(7, 0), grouped: false);
        var scenarioOpen = Make("Scenario", ShiftStatus.OriginalOrder, null, new DateOnly(2026, 6, 2), new TimeOnly(7, 0), grouped: false);
        scenarioOpen.AnalyseToken = Guid.NewGuid();
        _store.Seed(openOrder, sealedOrder, otherOpen, scenarioOpen);

        var (message, data) = await RunAsync();

        message.ShouldContain("has no orders linked to it");
        message.ShouldContain("group membership only");
        message.ShouldContain("never conclude from it that a place has no orders");
        message.ShouldContain("2 open (unsealed) and 1 sealed order(s) belong to no group at all");
        var scope = data.GetProperty("Scope");
        scope.GetProperty("UngroupedOpenOrders").GetInt32().ShouldBe(2);
        scope.GetProperty("UngroupedSealedOrders").GetInt32().ShouldBe(1);
    }

    [Test]
    public async Task OrdersInAPlaceNamedLikeTheGroup_AreReportedAsTheAddressDifference()
    {
        _mediator.Send(Arg.Is<GetTruncatedListQuery>(q => q.Filter.City == GroupName), Arg.Any<CancellationToken>())
            .Returns(
                new TruncatedShiftResource { Shifts = new List<ShiftResource>(), MaxItems = 133 },
                new TruncatedShiftResource { Shifts = new List<ShiftResource>(), MaxItems = 4 });

        var (message, data) = await RunAsync();

        message.ShouldContain($"137 order(s) are in the city '{GroupName}' and 0 in the state '{GroupName}'");
        message.ShouldContain("(137 by address, 0 by group)");
        message.ShouldContain("whether one of the orders found by address should be used");
        data.GetProperty("Scope").GetProperty("OrdersByAddressInCity").GetInt32().ShouldBe(137);
        await _mediator.Received(1).Send(
            Arg.Is<GetTruncatedListQuery>(q => q.Filter.City == GroupName
                && q.Filter.FilterType == ShiftFilterType.Original && !q.Filter.IsSealedOrder),
            Arg.Any<CancellationToken>());
        await _mediator.Received(1).Send(
            Arg.Is<GetTruncatedListQuery>(q => q.Filter.City == GroupName
                && q.Filter.FilterType == ShiftFilterType.Original && q.Filter.IsSealedOrder),
            Arg.Any<CancellationToken>());
        await _mediator.Received(2).Send(
            Arg.Is<GetTruncatedListQuery>(q => q.Filter.State == GroupName && q.Filter.City == null),
            Arg.Any<CancellationToken>());
    }

    private async Task<(string Message, JsonElement Data)> RunAsync()
    {
        var result = await _sut.ExecuteAsync(
            new SkillExecutionContext { UserId = Guid.NewGuid(), TenantId = Guid.NewGuid(), UserName = "planner", UserPermissions = [] },
            new Dictionary<string, object> { ["groupName"] = GroupName },
            CancellationToken.None);

        result.Success.ShouldBeTrue(result.Message);
        return (result.Message, JsonSerializer.SerializeToElement(result.Data));
    }

    private async Task<List<JsonElement>> OrdersAsync()
    {
        var result = await _sut.ExecuteAsync(
            new SkillExecutionContext { UserId = Guid.NewGuid(), TenantId = Guid.NewGuid(), UserName = "planner", UserPermissions = [] },
            new Dictionary<string, object> { ["groupName"] = GroupName },
            CancellationToken.None);

        result.Success.ShouldBeTrue(result.Message);
        using var document = JsonDocument.Parse(JsonSerializer.Serialize(result.Data));
        return document.RootElement.GetProperty("Orders").EnumerateArray().Select(o => o.Clone()).ToList();
    }

    private static Shift Make(
        string name, ShiftStatus status, Guid? originalId, DateOnly fromDate, TimeOnly start, Guid? groupId = null,
        bool grouped = true)
    {
        var id = Guid.NewGuid();
        return new Shift
        {
            Id = id,
            Name = name,
            Abbreviation = name,
            Status = status,
            ShiftType = ShiftType.IsTask,
            OriginalId = originalId,
            FromDate = fromDate,
            StartShift = start,
            EndShift = start.AddHours(12),
            GroupItems = grouped ? [new GroupItem { Id = Guid.NewGuid(), GroupId = groupId ?? GroupId, ShiftId = id }] : [],
        };
    }
}
