// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the apply contract: one transaction, the group is created before anything is staged, adds are
/// persisted and verified before any removal, existing memberships are skipped, and a failed
/// verification throws so the transaction rolls back.
/// </summary>

using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;

namespace Klacks.UnitTest.Application.Services.Grouping;

[TestFixture]
public class GroupingPlanApplierTests
{
    private static readonly DateTime ValidFrom = new(2026, 10, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();

    private IGroupRepository _groups = null!;
    private IGroupItemRepository _items = null!;
    private IUnitOfWork _unitOfWork = null!;
    private GroupingPlanApplier _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _groups = Substitute.For<IGroupRepository>();
        _items = Substitute.For<IGroupItemRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<GroupingApplyResult>>>())
            .Returns(call => call.Arg<Func<Task<GroupingApplyResult>>>()());
        _items.GetGroupIdsByShiftId(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(new List<Guid>());
        _items.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<IReadOnlyCollection<Guid>>().Count);
        _groups.Exists(Arg.Any<Guid>()).Returns(true);
        _sut = new GroupingPlanApplier(_groups, _items, _unitOfWork);
    }

    private static GroupingApplyCommand Command(params GroupingProposal[] proposals) =>
        new(proposals, ValidFrom, "Planung", "tester");

    [Test]
    public async Task AddsArePersistedAndVerifiedBeforeAnyRemoval()
    {
        var existing = new GroupItem { Id = Guid.NewGuid(), ClientId = ClientId, GroupId = GroupId };
        var removeTarget = Guid.NewGuid();
        _items.GetByClientAndGroup(ClientId, removeTarget).Returns(existing);
        _items.CountExistingByIds(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(existing.Id)), Arg.Any<CancellationToken>())
            .Returns(0);

        var result = await _sut.ApplyAsync(Command(
            new GroupingProposal(GroupingProposalKind.RemoveClient, removeTarget, null, ClientId, null, GroupingFindingCode.ClientDeadMembership),
            new GroupingProposal(GroupingProposalKind.AddClient, GroupId, null, ClientId, null, GroupingFindingCode.ShiftUncoveredInGroup)),
            CancellationToken.None);

        Received.InOrder(() =>
        {
            _items.Add(Arg.Is<GroupItem>(item => item.GroupId == GroupId && item.ClientId == ClientId && item.ValidFrom == ValidFrom));
            _unitOfWork.CompleteAsync();
            _items.Delete(existing.Id);
            _unitOfWork.CompleteAsync();
        });
        result.AddedClients.ShouldBe(1);
        result.RemovedClients.ShouldBe(1);
    }

    [Test]
    public async Task CreateGroup_HappensFirst_AndNewGroupProposalsTargetIt()
    {
        var result = await _sut.ApplyAsync(Command(
            new GroupingProposal(GroupingProposalKind.AddShift, null, GroupingFeasibilityDefaults.NewGroupKey, null, ShiftId, GroupingFindingCode.ShiftWithoutGroup),
            new GroupingProposal(GroupingProposalKind.CreateGroup, null, GroupingFeasibilityDefaults.NewGroupKey, null, null, GroupingFindingCode.ShiftWithoutGroup)),
            CancellationToken.None);

        result.CreatedGroups.ShouldBe(1);
        result.CreatedGroupId.ShouldNotBeNull();
        Received.InOrder(() =>
        {
            _groups.Add(Arg.Is<Group>(group => group.Name == "Planung"));
            _items.Add(Arg.Is<GroupItem>(item => item.ShiftId == ShiftId && item.GroupId == result.CreatedGroupId));
        });
    }

    [Test]
    public async Task ExistingMembership_IsSkipped()
    {
        _items.GetByClientAndGroup(ClientId, GroupId).Returns(new GroupItem { Id = Guid.NewGuid(), ClientId = ClientId, GroupId = GroupId });

        var result = await _sut.ApplyAsync(Command(
            new GroupingProposal(GroupingProposalKind.AddClient, GroupId, null, ClientId, null, GroupingFindingCode.ClientWithoutGroup)),
            CancellationToken.None);

        result.AddedClients.ShouldBe(0);
        result.AlreadyInPlace.ShouldBe(1);
        await _items.DidNotReceive().Add(Arg.Any<GroupItem>());
    }

    [Test]
    public async Task FailedVerificationOfAdds_Throws()
    {
        _items.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(0);

        await Should.ThrowAsync<SkillVerificationException>(() => _sut.ApplyAsync(Command(
            new GroupingProposal(GroupingProposalKind.AddClient, GroupId, null, ClientId, null, GroupingFindingCode.ClientWithoutGroup)),
            CancellationToken.None));
    }

    [Test]
    public async Task CreateGroupWithoutName_Throws()
    {
        var command = new GroupingApplyCommand(
            [new GroupingProposal(GroupingProposalKind.CreateGroup, null, GroupingFeasibilityDefaults.NewGroupKey, null, null, GroupingFindingCode.ShiftWithoutGroup)],
            ValidFrom, null, "tester");

        await Should.ThrowAsync<ArgumentException>(() => _sut.ApplyAsync(command, CancellationToken.None));
    }

    [Test]
    public async Task NewGroupTargetWithoutCreateGroup_ThrowsBeforeTouchingTheDatabase()
    {
        var command = Command(
            new GroupingProposal(GroupingProposalKind.AddShift, null, GroupingFeasibilityDefaults.NewGroupKey, null, ShiftId, GroupingFindingCode.ShiftWithoutGroup));

        await Should.ThrowAsync<ArgumentException>(() => _sut.ApplyAsync(command, CancellationToken.None));
        await _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<Task<GroupingApplyResult>>>());
    }
}
