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
/// find_split_shift_candidates over the real ShiftRepository (EF InMemory): only real sealed orders, plannable shifts and
/// split parts with a live real link to a group in the scope of the caller; rows grouped by their order (OriginalId, else their own id); the
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
    private IGroupScopeGuard _scopeGuard = null!;
    private FindSplitShiftCandidatesSkill _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new InMemoryShiftStore();
        _groupSearch = Substitute.For<IGroupSearchRepository>();
        GroupSearchAnswers(new GroupSearchItem { Id = GroupId, Name = GroupName });
        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<GetTruncatedListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new TruncatedShiftResource { Shifts = new List<ShiftResource>(), MaxItems = 0 });
        _scopeGuard = Substitute.For<IGroupScopeGuard>();
        _scopeGuard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Unrestricted());
        _sut = new FindSplitShiftCandidatesSkill(_store.Reader, _groupSearch, _mediator, _scopeGuard);
    }

    [TearDown]
    public void TearDown() => _store.Dispose();

    private void GroupSearchAnswers(params GroupSearchItem[] items) =>
        _groupSearch.SearchWithinRootsAsync(
                Arg.Any<string?>(), Arg.Any<int>(), Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<CancellationToken>())
            .Returns(new GroupSearchResult { Items = items.ToList(), TotalCount = items.Length });

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

    [Test]
    public async Task AKeyShiftAndAScenarioCopyOfAnOrder_AreNotListed()
    {
        var real = Make("Alpha", ShiftStatus.SealedOrder, originalId: null, new DateOnly(2026, 6, 1), new TimeOnly(7, 0));
        var scenarioCopy = Make("Alpha", ShiftStatus.SealedOrder, originalId: null, new DateOnly(2026, 6, 1), new TimeOnly(7, 0));
        scenarioCopy.AnalyseToken = Guid.NewGuid();
        scenarioCopy.ScenarioSourceShiftId = real.Id;
        var keyShift = Make("Confidential", ShiftStatus.SealedOrder, originalId: null, new DateOnly(2026, 6, 1), new TimeOnly(7, 0));
        keyShift.AnalyseToken = Guid.NewGuid();
        _store.Seed(real, scenarioCopy, keyShift);

        var orders = await OrdersAsync();

        orders.Select(o => o.GetProperty("SealedOrderId").GetGuid()).ShouldBe([real.Id]);
    }

    [Test]
    public async Task AGroupOutsideTheCallersScope_IsAnsweredLikeNoGroupAtAll_AndNothingIsRead()
    {
        GroupSearchAnswers();
        _scopeGuard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Restricted([OtherGroupId], ["Other"]));
        _store.Seed(Make("Alpha", ShiftStatus.SealedOrder, originalId: null, new DateOnly(2026, 6, 1), new TimeOnly(7, 0)));

        var result = await _sut.ExecuteAsync(
            new SkillExecutionContext { UserId = Guid.NewGuid(), TenantId = Guid.NewGuid(), UserName = "planner", UserPermissions = [] },
            new Dictionary<string, object> { ["groupName"] = GroupName },
            CancellationToken.None);

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"No group found matching '{GroupName}'.");
    }

    [Test]
    public async Task AmbiguousMatches_NameOnlyTheGroupsInTheCallersScope()
    {
        var visibleRootId = Guid.NewGuid();
        GroupSearchAnswers(
            new GroupSearchItem { Id = visibleRootId, Name = "Open " + GroupName },
            new GroupSearchItem { Id = OtherGroupId, Name = "Open child " + GroupName, Root = visibleRootId });
        _scopeGuard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Restricted([visibleRootId], ["Open"]));

        var (message, data) = await RunAsync();

        message.ShouldContain("Multiple groups match");
        data.GetProperty("Matches").EnumerateArray().Select(match => match.GetProperty("Id").GetGuid())
            .ShouldBe([visibleRootId, OtherGroupId]);
        await _groupSearch.Received(1).SearchWithinRootsAsync(
            GroupName, Arg.Any<int>(),
            Arg.Is<IReadOnlyCollection<Guid>?>(roots => roots != null && roots.SequenceEqual(new[] { visibleRootId })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnUnrestrictedCaller_SearchesWithoutARootScope()
    {
        _store.Seed(Make("Alpha", ShiftStatus.SealedOrder, originalId: null, new DateOnly(2026, 6, 1), new TimeOnly(7, 0)));

        await OrdersAsync();

        await _groupSearch.Received(1).SearchWithinRootsAsync(
            GroupName, Arg.Any<int>(), Arg.Is<IReadOnlyCollection<Guid>?>(roots => roots == null), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OneVisibleMatch_IsReadAsThatGroup()
    {
        _scopeGuard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Restricted([GroupId], [GroupName]));
        _store.Seed(Make("Alpha", ShiftStatus.SealedOrder, originalId: null, new DateOnly(2026, 6, 1), new TimeOnly(7, 0)));

        var orders = await OrdersAsync();

        orders.Select(o => o.GetProperty("Name").GetString()).ShouldBe(["Alpha"]);
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
    public async Task OrdersInAPlaceNamedLikeTheGroup_AreReportedPerCityAndState_OpenAndSealedApart_NeverSummed()
    {
        StubCount(q => q.Filter.City == GroupName && !q.Filter.IsSealedOrder, 133);
        StubCount(q => q.Filter.City == GroupName && q.Filter.IsSealedOrder, 4);
        StubCount(q => q.Filter.State == GroupName && !q.Filter.IsSealedOrder, 400);
        StubCount(q => q.Filter.State == GroupName && q.Filter.IsSealedOrder, 9);

        var (message, data) = await RunAsync();

        message.ShouldContain($"city '{GroupName}' 133 open and 4 sealed order(s)");
        message.ShouldContain($"state '{GroupName}' 400 open and 9 sealed order(s)");
        message.ShouldContain("never add the two up");
        message.ShouldContain("difference to the 0 sealed order(s) linked to the group");
        message.ShouldNotContain("137");
        message.ShouldNotContain("533");
        message.ShouldContain("whether one of the orders found by address should be used");
        var scope = data.GetProperty("Scope");
        scope.GetProperty("OrdersByAddressInCity").GetProperty("OpenOrders").GetInt32().ShouldBe(133);
        scope.GetProperty("OrdersByAddressInState").GetProperty("SealedOrders").GetInt32().ShouldBe(9);
        await _mediator.Received(4).Send(
            Arg.Is<GetTruncatedListQuery>(q => q.Filter.FilterType == ShiftFilterType.Original
                && (q.Filter.City == null) != (q.Filter.State == null)),
            Arg.Any<CancellationToken>());
    }

    private void StubCount(Func<GetTruncatedListQuery, bool> matches, int count)
    {
        _mediator.Send(Arg.Is<GetTruncatedListQuery>(q => matches(q)), Arg.Any<CancellationToken>())
            .Returns(new TruncatedShiftResource { Shifts = new List<ShiftResource>(), MaxItems = count });
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
