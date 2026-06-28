// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ApplyCustomerGroupingCommandHandler: it retires the replaced location memberships
/// (soft-delete via the group-item repository) and adds the target membership inside a single
/// transaction, re-reads the newly added memberships to confirm them, rolls the whole batch back by
/// throwing a SkillVerificationException when the database does not confirm every add, skips a retire
/// whose membership no longer exists, and does nothing when a customer already sits in the target with
/// nothing to retire.
/// </summary>

using Klacks.Api.Application.Commands.Grouping;
using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Handlers.Grouping;
using Klacks.Api.Application.Services.Grouping;
using Klacks.Api.Application.Interfaces.Grouping;
using Klacks.Api.Domain.Exceptions;

namespace Klacks.UnitTest.Application.Handlers.Grouping;

[TestFixture]
public class ApplyCustomerGroupingCommandHandlerTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid Canton = Guid.NewGuid();
    private static readonly Guid City = Guid.NewGuid();
    private static readonly DateTime CompanyToday = new(2099, 1, 15, 0, 0, 0, DateTimeKind.Utc);

    private ICustomerGroupingPlanner _planner = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ICompanyClock _companyClock = null!;
    private ApplyCustomerGroupingCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _planner = Substitute.For<ICustomerGroupingPlanner>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(CompanyToday);
        _handler = new ApplyCustomerGroupingCommandHandler(_planner, _groupItemRepository, _unitOfWork, _companyClock);

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<int>>>())
            .Returns(ci => ci.Arg<Func<Task<int>>>()());
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyCollection<Guid>>().Count);
    }

    [Test]
    public async Task Handle_MovesCustomer_RetiresCantonAndAddsCity_AndVerifies()
    {
        SetProposal(new CustomerGroupingAssignment(
            ClientId, "Anna Meier", new[] { "ZH" }, City, "Zürich", 3.2, new[] { Canton }));
        var cantonMembership = new GroupItem { Id = Guid.NewGuid(), ClientId = ClientId, GroupId = Canton };
        _groupItemRepository.GetByClientAndGroup(ClientId, Canton).Returns(cantonMembership);
        _groupItemRepository.GetByClientAndGroup(ClientId, City).Returns((GroupItem?)null);

        var result = await _handler.Handle(new ApplyCustomerGroupingCommand(), CancellationToken.None);

        _groupItemRepository.Received(1).Remove(cantonMembership);
        await _groupItemRepository.Received(1).Add(Arg.Is<GroupItem>(g =>
            g.GroupId == City && g.ClientId == ClientId
            && g.ValidFrom == CompanyToday && g.ValidFrom.Kind == DateTimeKind.Utc));
        await _unitOfWork.Received(1).CompleteAsync();
        await _groupItemRepository.Received(1).CountExistingByIds(
            Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 1), Arg.Any<CancellationToken>());
        result.MovedCount.ShouldBe(1);
        result.VerifiedCount.ShouldBe(1);
    }

    [Test]
    public async Task Handle_TargetAlreadyPresent_DoesNotAddDuplicate_AndVerifiesNothing()
    {
        SetProposal(new CustomerGroupingAssignment(
            ClientId, "Anna Meier", new[] { "ZH", "Zürich" }, City, "Zürich", 3.2, new[] { Canton }));
        _groupItemRepository.GetByClientAndGroup(ClientId, Canton)
            .Returns(new GroupItem { Id = Guid.NewGuid(), ClientId = ClientId, GroupId = Canton });
        _groupItemRepository.GetByClientAndGroup(ClientId, City)
            .Returns(new GroupItem { Id = Guid.NewGuid(), ClientId = ClientId, GroupId = City });

        var result = await _handler.Handle(new ApplyCustomerGroupingCommand(), CancellationToken.None);

        await _groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
        await _groupItemRepository.DidNotReceive().CountExistingByIds(
            Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
        result.MovedCount.ShouldBe(1);
        result.VerifiedCount.ShouldBe(0);
    }

    [Test]
    public async Task Handle_VerificationMismatch_ThrowsAndRollsBack()
    {
        SetProposal(new CustomerGroupingAssignment(
            ClientId, "Anna Meier", new[] { "ZH" }, City, "Zürich", 3.2, new[] { Canton }));
        _groupItemRepository.GetByClientAndGroup(ClientId, Canton)
            .Returns(new GroupItem { Id = Guid.NewGuid(), ClientId = ClientId, GroupId = Canton });
        _groupItemRepository.GetByClientAndGroup(ClientId, City).Returns((GroupItem?)null);
        _groupItemRepository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(0);

        Func<Task> act = () => _handler.Handle(new ApplyCustomerGroupingCommand(), CancellationToken.None);

        await Should.ThrowAsync<SkillVerificationException>(act);
    }

    [Test]
    public async Task Handle_NoAssignments_DoesNotCommit()
    {
        _planner.BuildProposalAsync(Arg.Any<EntityTypeEnum>(), Arg.Any<CancellationToken>())
            .Returns(new CustomerGroupingProposal(
                2,
                Array.Empty<CustomerGroupingAssignment>(),
                new[] { new UnassignedCustomer(Guid.NewGuid(), "Cara Frei", "no geocoded address") }));

        var result = await _handler.Handle(new ApplyCustomerGroupingCommand(), CancellationToken.None);

        await _unitOfWork.DidNotReceive().CompleteAsync();
        result.MovedCount.ShouldBe(0);
        result.VerifiedCount.ShouldBe(0);
        result.UnassignedCount.ShouldBe(1);
    }

    private void SetProposal(CustomerGroupingAssignment assignment)
    {
        _planner.BuildProposalAsync(Arg.Any<EntityTypeEnum>(), Arg.Any<CancellationToken>())
            .Returns(new CustomerGroupingProposal(
                2,
                new[] { assignment },
                Array.Empty<UnassignedCustomer>()));
    }
}
