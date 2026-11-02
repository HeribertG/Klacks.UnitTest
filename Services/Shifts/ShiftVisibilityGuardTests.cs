// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The shift write guard follows the shift read restriction: an unrestricted caller (null restriction) passes without
/// a shift query, a restricted caller may touch only existing visible shifts (missing and hidden look alike), and a
/// write may only add, remove or re-date assignments of visible groups while unchanged hidden assignments survive.
/// </summary>

using Klacks.Api.Application.Services.Shifts;
using Klacks.Api.Domain.Services.Shifts;
using Klacks.Api.Domain.Services.ShiftSchedule;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Services.Shifts;

[TestFixture]
public class ShiftVisibilityGuardTests
{
    private static readonly Guid VisibleGroup = Guid.NewGuid();
    private static readonly Guid HiddenGroup = Guid.NewGuid();

    private IShiftGroupFilterService _filter = null!;
    private IShiftVisibilityReader _repository = null!;
    private IGroupVisibilityGuard _groupGuard = null!;
    private ShiftVisibilityGuard _guard = null!;

    [SetUp]
    public void SetUp()
    {
        _filter = Substitute.For<IShiftGroupFilterService>();
        _repository = Substitute.For<IShiftVisibilityReader>();
        _groupGuard = Substitute.For<IGroupVisibilityGuard>();
        _groupGuard.IsGroupVisibleAsync(VisibleGroup, Arg.Any<CancellationToken>()).Returns(true);
        _groupGuard.IsGroupVisibleAsync(HiddenGroup, Arg.Any<CancellationToken>()).Returns(false);
        _groupGuard.AreAllGroupsVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyCollection<Guid>>().All(id => id == VisibleGroup));
        _guard = new ShiftVisibilityGuard(_filter, _repository, _groupGuard, Substitute.For<ILogger<ShiftVisibilityGuard>>());
    }

    private void Restricted() =>
        _filter.ResolveShiftListGroupIdsAsync(null).Returns(Task.FromResult<List<Guid>?>([VisibleGroup]));

    private void Unrestricted() =>
        _filter.ResolveShiftListGroupIdsAsync(null).Returns(Task.FromResult<List<Guid>?>(null));

    private void VisibleShiftsAre(params Guid[] ids) =>
        _repository.FilterVisibleShiftIdsAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.ArgAt<IReadOnlyCollection<Guid>>(0).Where(ids.Contains).ToList());

    private static GroupItem Item(Guid groupId, DateTime? from = null) =>
        new() { GroupId = groupId, ValidFrom = from };

    private static Shift ShiftIn(params Guid[] groupIds) =>
        new() { Id = Guid.NewGuid(), GroupItems = groupIds.Select(id => Item(id)).ToList() };

    [Test]
    public async Task Unrestricted_PassesEveryShift_WithoutShiftQuery()
    {
        Unrestricted();

        (await _guard.IsVisibleAsync(Guid.NewGuid())).ShouldBeTrue();
        (await _guard.FindFirstHiddenAsync([Guid.NewGuid(), Guid.NewGuid()])).ShouldBeNull();
        await _repository.DidNotReceiveWithAnyArgs().FilterVisibleShiftIdsAsync(default!, default!, default);
    }

    [Test]
    public async Task FilterVisible_Unrestricted_ReturnsAllDistinctIds_WithoutShiftQuery()
    {
        Unrestricted();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();

        var visible = await _guard.FilterVisibleAsync([first, second, first]);

        visible.ShouldBe([first, second], ignoreOrder: true);
        await _repository.DidNotReceiveWithAnyArgs().FilterVisibleShiftIdsAsync(default!, default!, default);
    }

    [Test]
    public async Task FindFirstNotReferable_Unrestricted_FindsAMissingOrTokenedId()
    {
        Unrestricted();
        var real = Guid.NewGuid();
        var notReal = Guid.NewGuid();
        _repository.FilterRealShiftIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), CancellationToken.None).Returns([real]);

        (await _guard.FindFirstNotReferableAsync([real, notReal], CancellationToken.None)).ShouldBe(notReal);
    }

    [Test]
    public async Task FindFirstNotReferable_Restricted_FindsAHiddenRealId()
    {
        Restricted();
        var visible = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        VisibleShiftsAre(visible);
        _repository.FilterRealShiftIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), CancellationToken.None).Returns([visible, hidden]);

        (await _guard.FindFirstNotReferableAsync([visible, hidden], CancellationToken.None)).ShouldBe(hidden);
    }

    [Test]
    public async Task FindFirstNotReferable_WhenAllAreRealAndVisible_ReturnsNull()
    {
        Restricted();
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        VisibleShiftsAre(first, second);
        _repository.FilterRealShiftIdsAsync(Arg.Any<IReadOnlyCollection<Guid>>(), CancellationToken.None).Returns([first, second]);

        (await _guard.FindFirstNotReferableAsync([first, second], CancellationToken.None)).ShouldBeNull();
    }

    [Test]
    public async Task FindFirstNotReferable_WithoutIds_ReturnsNull_WithoutAQuery()
    {
        Unrestricted();

        (await _guard.FindFirstNotReferableAsync([], CancellationToken.None)).ShouldBeNull();

        await _repository.DidNotReceiveWithAnyArgs().FilterRealShiftIdsAsync(default!, default);
    }

    [Test]
    public async Task FindFirstTokened_Unrestricted_StillAsksTheReader_AndReturnsItsAnswer()
    {
        Unrestricted();
        var tokened = Guid.NewGuid();
        var ids = new[] { Guid.NewGuid(), tokened };
        _repository.FindFirstTokenedShiftIdAsync(ids, CancellationToken.None).Returns(tokened);

        (await _guard.FindFirstTokenedAsync(ids, CancellationToken.None)).ShouldBe(tokened);

        await _repository.Received(1).FindFirstTokenedShiftIdAsync(ids, CancellationToken.None);
    }

    [Test]
    public async Task FindFirstTokened_Restricted_AnswersFromTheReaderToo()
    {
        Restricted();
        var tokened = Guid.NewGuid();
        var ids = new[] { tokened };
        _repository.FindFirstTokenedShiftIdAsync(ids, CancellationToken.None).Returns(tokened);

        (await _guard.FindFirstTokenedAsync(ids, CancellationToken.None)).ShouldBe(tokened);
    }

    [Test]
    public async Task FindFirstTokened_WhenNoShiftCarriesAToken_ReturnsNull()
    {
        Unrestricted();
        var ids = new[] { Guid.NewGuid() };
        _repository.FindFirstTokenedShiftIdAsync(ids, CancellationToken.None).Returns((Guid?)null);

        (await _guard.FindFirstTokenedAsync(ids, CancellationToken.None)).ShouldBeNull();
    }

    [Test]
    public async Task FilterVisible_Restricted_ReturnsOnlyWhatTheReaderPasses_WithOneQuery()
    {
        Restricted();
        var visibleId = Guid.NewGuid();
        var hiddenOrMissing = Guid.NewGuid();
        VisibleShiftsAre(visibleId);

        var visible = await _guard.FilterVisibleAsync([visibleId, hiddenOrMissing, visibleId]);

        visible.ShouldBe([visibleId]);
        await _repository.Received(1).FilterVisibleShiftIdsAsync(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2),
            Arg.Is<IReadOnlyCollection<Guid>>(groups => groups.SequenceEqual(new[] { VisibleGroup })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FilterVisible_EmptyInput_ReturnsEmpty_WithoutAnyQuery()
    {
        Restricted();

        (await _guard.FilterVisibleAsync([])).ShouldBeEmpty();
        await _filter.DidNotReceiveWithAnyArgs().ResolveShiftListGroupIdsAsync(default);
        await _repository.DidNotReceiveWithAnyArgs().FilterVisibleShiftIdsAsync(default!, default!, default);
    }

    [Test]
    public async Task Restricted_VisibleShift_Passes_HiddenAndMissingAreRefusedAlike()
    {
        Restricted();
        var visible = Guid.NewGuid();
        var hiddenOrMissing = Guid.NewGuid();
        VisibleShiftsAre(visible);

        (await _guard.IsVisibleAsync(visible)).ShouldBeTrue();
        (await _guard.IsVisibleAsync(hiddenOrMissing)).ShouldBeFalse();
        (await _guard.FindFirstHiddenAsync([visible, hiddenOrMissing])).ShouldBe(hiddenOrMissing);
        (await _guard.FindFirstHiddenAsync([visible])).ShouldBeNull();
    }

    [Test]
    public async Task Restricted_PassesTheReadRestrictionToTheRepository()
    {
        Restricted();
        VisibleShiftsAre();

        await _guard.IsVisibleAsync(Guid.NewGuid());

        await _repository.Received(1).FilterVisibleShiftIdsAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(),
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.SequenceEqual(new[] { VisibleGroup })),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LoadedShift_GroupLessAndMixedAreVisible_HiddenOnlyIsNot()
    {
        Restricted();

        (await _guard.IsVisibleAsync(ShiftIn())).ShouldBeTrue();
        (await _guard.IsVisibleAsync(ShiftIn(HiddenGroup, VisibleGroup))).ShouldBeTrue();
        (await _guard.IsVisibleAsync(ShiftIn(HiddenGroup))).ShouldBeFalse();
    }

    [Test]
    public async Task GroupChanges_UnchangedHiddenAssignment_Survives()
    {
        var stored = new List<GroupItem> { Item(HiddenGroup), Item(VisibleGroup) };
        var requested = new List<GroupItem> { Item(HiddenGroup), Item(VisibleGroup) };

        await Should.NotThrowAsync(() => _guard.EnsureGroupChangesVisibleAsync(stored, requested));
    }

    [Test]
    public async Task GroupChanges_AddingVisibleGroup_Passes()
    {
        await Should.NotThrowAsync(() => _guard.EnsureGroupChangesVisibleAsync([], [Item(VisibleGroup)]));
    }

    [Test]
    public async Task GroupChanges_AddingHiddenGroup_IsAnsweredLikeAMissingGroup()
    {
        var error = await Should.ThrowAsync<KeyNotFoundException>(
            () => _guard.EnsureGroupChangesVisibleAsync([], [Item(VisibleGroup), Item(HiddenGroup)]));

        error.Message.ShouldBe(string.Format(ShiftVisibilityMessages.GroupNotFound, HiddenGroup));
    }

    [Test]
    public async Task GroupChanges_RemovingHiddenGroup_IsRefused()
    {
        await Should.ThrowAsync<KeyNotFoundException>(
            () => _guard.EnsureGroupChangesVisibleAsync([Item(HiddenGroup), Item(VisibleGroup)], [Item(VisibleGroup)]));
    }

    [Test]
    public async Task GroupChanges_RedatingHiddenGroup_IsRefused()
    {
        var stored = new List<GroupItem> { Item(HiddenGroup, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)) };
        var requested = new List<GroupItem> { Item(HiddenGroup, new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc)) };

        await Should.ThrowAsync<KeyNotFoundException>(() => _guard.EnsureGroupChangesVisibleAsync(stored, requested));
    }

    [Test]
    public async Task GroupChangesByShiftId_UnrestrictedCaller_DoesNotReadStoredGroups()
    {
        _groupGuard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(true);

        await _guard.EnsureGroupChangesVisibleAsync(Guid.NewGuid(), [Item(HiddenGroup)]);

        await _repository.DidNotReceiveWithAnyArgs().GetActiveGroupItemsAsync(default, default);
    }

    [Test]
    public async Task GroupChangesByShiftId_ComparesWithStoredGroups()
    {
        var shiftId = Guid.NewGuid();
        _groupGuard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(false);
        _repository.GetActiveGroupItemsAsync(shiftId, Arg.Any<CancellationToken>())
            .Returns([Item(HiddenGroup), Item(VisibleGroup)]);

        await Should.NotThrowAsync(
            () => _guard.EnsureGroupChangesVisibleAsync(shiftId, [Item(HiddenGroup), Item(VisibleGroup)]));
        await Should.ThrowAsync<KeyNotFoundException>(
            () => _guard.EnsureGroupChangesVisibleAsync(shiftId, [Item(VisibleGroup)]));
    }

    [Test]
    public void AssignmentChanges_ReportsAddedRemovedAndRedatedGroupsOnly()
    {
        var unchanged = Guid.NewGuid();
        var added = Guid.NewGuid();
        var removed = Guid.NewGuid();
        var redated = Guid.NewGuid();
        var stored = new List<GroupItem> { Item(unchanged), Item(removed), Item(redated) };
        var requested = new List<GroupItem>
        {
            Item(unchanged), Item(added), Item(redated, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc))
        };

        ShiftGroupAssignmentChanges.ChangedGroupIds(stored, requested)
            .ShouldBe([added, removed, redated], ignoreOrder: true);
    }

    [Test]
    public void AssignmentChanges_MissingStartSentBackAsMinValue_IsNotARedate()
    {
        var group = Guid.NewGuid();
        var stored = new List<GroupItem> { Item(group) };
        var requested = new List<GroupItem> { Item(group, DateTime.MinValue) };

        ShiftGroupAssignmentChanges.ChangedGroupIds(stored, requested).ShouldBeEmpty();
    }

    [Test]
    public void AssignmentChanges_SameDayWithDifferentTime_IsNotARedate()
    {
        var group = Guid.NewGuid();
        var stored = new List<GroupItem> { Item(group, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc)) };
        var requested = new List<GroupItem> { Item(group, new DateTime(2026, 3, 1, 12, 0, 0, DateTimeKind.Utc)) };

        ShiftGroupAssignmentChanges.ChangedGroupIds(stored, requested).ShouldBeEmpty();
    }

    [Test]
    public void AssignmentChanges_IgnoresDeletedStoredItems()
    {
        var group = Guid.NewGuid();
        var stored = new List<GroupItem> { new() { GroupId = group, IsDeleted = true } };

        ShiftGroupAssignmentChanges.ChangedGroupIds(stored, []).ShouldBeEmpty();
    }
}
