// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the bulk group-item endpoint. Its reason to exist is the atomicity a caller cannot
/// provide across N separate requests, so that is what these pin: every row goes in inside one
/// transaction, and a batch that cannot be confirmed in full is rejected rather than reported as a
/// partial success — a partial count would invite the caller to retry the rest on top of rows that
/// are already there.
/// </summary>

using Klacks.Api.Application.Commands.Associations;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Handlers.Associations;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Handlers.Associations;

[TestFixture]
public class BulkAddGroupItemsCommandHandlerTests
{
    private static readonly Guid GroupId = Guid.NewGuid();

    private IGroupItemRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private BulkAddGroupItemsCommandHandler _handler = null!;
    private List<GroupItem> _added = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IGroupItemRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _added = [];

        _repository.Add(Arg.Do<GroupItem>(item => _added.Add(item))).Returns(Task.CompletedTask);
        // A substituted IUnitOfWork does not run the delegate on its own; without this passthrough the
        // handler's whole transaction body would silently never execute.
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<BulkGroupItemResponse>>>())
            .Returns(ci => ci.Arg<Func<Task<BulkGroupItemResponse>>>()());

        _handler = new BulkAddGroupItemsCommandHandler(
            _repository, _unitOfWork, NullLogger<BulkAddGroupItemsCommandHandler>.Instance);
    }

    private static BulkAddGroupItemsCommand Command(int count) =>
        new(new BulkGroupItemRequest
        {
            Items = Enumerable.Range(0, count).Select(_ => new GroupItemResource
            {
                Id = Guid.NewGuid(),
                ShiftId = Guid.NewGuid(),
                GroupId = GroupId,
                ValidFrom = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc)
            }).ToList()
        });

    [Test]
    public async Task Handle_AddsEveryRequestedRow()
    {
        _repository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(3);

        var response = await _handler.Handle(Command(3), CancellationToken.None);

        response.AddedCount.ShouldBe(3);
        response.AddedIds.Count.ShouldBe(3);
        _added.Count.ShouldBe(3);
        _added.ShouldAllBe(item => item.GroupId == GroupId);
    }

    [Test]
    public async Task Handle_RunsInsideOneTransaction()
    {
        _repository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(2);

        await _handler.Handle(Command(2), CancellationToken.None);

        await _unitOfWork.Received(1).ExecuteInTransactionAsync(Arg.Any<Func<Task<BulkGroupItemResponse>>>());
    }

    [Test]
    public async Task Handle_PartialConfirmation_ThrowsSoTheBatchRollsBack()
    {
        _repository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(2);

        var error = await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(Command(3), CancellationToken.None));

        error.Message.ShouldContain("rolled back");
        error.Message.ShouldContain("only 2");
    }

    [Test]
    public async Task Handle_EmptyRequest_TouchesNothing()
    {
        var response = await _handler.Handle(Command(0), CancellationToken.None);

        response.AddedCount.ShouldBe(0);
        _added.ShouldBeEmpty();
        await _unitOfWork.DidNotReceive().ExecuteInTransactionAsync(Arg.Any<Func<Task<BulkGroupItemResponse>>>());
    }

    [Test]
    public async Task Handle_ResourceWithoutId_GetsOneAssigned()
    {
        _repository.CountExistingByIds(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(1);

        var command = new BulkAddGroupItemsCommand(new BulkGroupItemRequest
        {
            Items = [new GroupItemResource { GroupId = GroupId, ShiftId = Guid.NewGuid() }]
        });

        var response = await _handler.Handle(command, CancellationToken.None);

        _added.Single().Id.ShouldNotBe(Guid.Empty);
        response.AddedIds.Single().ShouldBe(_added.Single().Id);
    }
}
