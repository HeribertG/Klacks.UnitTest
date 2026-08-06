// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the group-subtree endpoint. Its reason to exist is the atomicity a caller cannot
/// provide across one request per child, so that is what these pin: the children and the group itself
/// go in one transaction, and the response reports the whole count.
/// </summary>

using Klacks.Api.Application.Commands.Associations;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Handlers.Associations;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Handlers.Associations;

[TestFixture]
public class DeleteGroupSubtreeCommandHandlerTests
{
    private static readonly Guid GroupId = Guid.NewGuid();

    private IGroupRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private DeleteGroupSubtreeCommandHandler _handler = null!;
    private List<Guid> _deleted = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IGroupRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _deleted = [];

        _repository.Get(GroupId).Returns(new Group { Id = GroupId, Name = "Bern" });
        _repository.Delete(Arg.Do<Guid>(id => _deleted.Add(id))).Returns((Group?)null!);
        // A substituted IUnitOfWork does not run the delegate; without this the handler body never runs.
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<DeleteGroupSubtreeResponse>>>())
            .Returns(ci => ci.Arg<Func<Task<DeleteGroupSubtreeResponse>>>()());

        _handler = new DeleteGroupSubtreeCommandHandler(
            _repository, _unitOfWork, NullLogger<DeleteGroupSubtreeCommandHandler>.Instance);
    }

    private void WireChildren(params Guid[] ids) =>
        _repository.GetChildren(GroupId).Returns(ids.Select(id => new Group { Id = id, Name = "child" }).ToList());

    [Test]
    public async Task Handle_RemovesEveryChildAndTheGroupItself()
    {
        var first = Guid.NewGuid();
        var second = Guid.NewGuid();
        WireChildren(first, second);

        var response = await _handler.Handle(new DeleteGroupSubtreeCommand(GroupId), CancellationToken.None);

        response.DeletedCount.ShouldBe(3);
        response.DeletedGroupName.ShouldBe("Bern");
        _deleted.ShouldBe([first, second, GroupId]);
    }

    [Test]
    public async Task Handle_RunsInsideOneTransaction()
    {
        WireChildren(Guid.NewGuid());

        await _handler.Handle(new DeleteGroupSubtreeCommand(GroupId), CancellationToken.None);

        await _unitOfWork.Received(1)
            .ExecuteInTransactionAsync(Arg.Any<Func<Task<DeleteGroupSubtreeResponse>>>());
    }

    [Test]
    public async Task Handle_GroupWithoutChildren_RemovesJustIt()
    {
        WireChildren();

        var response = await _handler.Handle(new DeleteGroupSubtreeCommand(GroupId), CancellationToken.None);

        response.DeletedCount.ShouldBe(1);
        _deleted.ShouldBe([GroupId]);
    }

    [Test]
    public async Task Handle_UnknownGroup_Throws()
    {
        _repository.Get(GroupId).Returns((Group?)null);

        await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new DeleteGroupSubtreeCommand(GroupId), CancellationToken.None));

        _deleted.ShouldBeEmpty();
    }
}
