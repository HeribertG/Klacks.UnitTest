// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// POST api/backend/Clients persists the group memberships sent with the new client. Until 2026-10-01 a
/// group-restricted caller could therefore create a client directly inside a group outside their visibility.
/// Every requested group must now be visible; a hidden group is answered exactly like a missing one and
/// nothing is written. Admins and background callers are unrestricted inside the guard.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Handlers.Clients;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Clients;

[TestFixture]
public class PostClientCommandHandlerVisibilityTests
{
    private IClientRepository _clientRepository = null!;
    private IUnitOfWork _unitOfWork = null!;

    [SetUp]
    public void SetUp()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
    }

    private PostCommandHandler CreateHandler(IGroupVisibilityGuard groupVisibilityGuard)
    {
        return new PostCommandHandler(
            _clientRepository,
            new ClientMapper(),
            _unitOfWork,
            Substitute.For<IEmailClientAssignmentService>(),
            groupVisibilityGuard,
            Substitute.For<ILogger<PostCommandHandler>>());
    }

    private static ClientResource NewClientIn(params Guid[] groupIds)
    {
        var resource = new ClientResource { Id = Guid.NewGuid(), Name = "Steiner", FirstName = "Anna" };
        foreach (var groupId in groupIds)
        {
            resource.GroupItems.Add(new ClientGroupItemResource { GroupId = groupId });
        }

        return resource;
    }

    [Test]
    public async Task ClientInsideAHiddenGroup_IsRefusedLikeAMissingGroup_AndNothingIsWritten()
    {
        var visibleGroupId = Guid.NewGuid();
        var hiddenGroupId = Guid.NewGuid();
        var guard = Substitute.For<IGroupVisibilityGuard>();
        guard.AreAllGroupsVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(false);
        guard.IsGroupVisibleAsync(visibleGroupId, Arg.Any<CancellationToken>()).Returns(true);
        guard.IsGroupVisibleAsync(hiddenGroupId, Arg.Any<CancellationToken>()).Returns(false);

        var exception = await Should.ThrowAsync<KeyNotFoundException>(() => CreateHandler(guard)
            .Handle(new PostCommand<ClientResource>(NewClientIn(visibleGroupId, hiddenGroupId)), CancellationToken.None));

        exception.Message.ShouldContain(hiddenGroupId.ToString());
        await _clientRepository.DidNotReceive().Add(Arg.Any<Client>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task ClientInsideVisibleGroups_IsCreated()
    {
        var groupId = Guid.NewGuid();

        var result = await CreateHandler(TestGroupWriteVisibility.UnrestrictedGroups())
            .Handle(new PostCommand<ClientResource>(NewClientIn(groupId)), CancellationToken.None);

        result.ShouldNotBeNull();
        await _clientRepository.Received(1).Add(Arg.Is<Client>(c => c.GroupItems.Any(g => g.GroupId == groupId)));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task ClientWithoutGroups_IsCreated_WithoutAskingForVisibleGroups()
    {
        var guard = Substitute.For<IGroupVisibilityGuard>();
        guard.AreAllGroupsVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(ci.Arg<IReadOnlyCollection<Guid>>().Count == 0));

        var result = await CreateHandler(guard)
            .Handle(new PostCommand<ClientResource>(NewClientIn()), CancellationToken.None);

        result.ShouldNotBeNull();
        await _clientRepository.Received(1).Add(Arg.Any<Client>());
        await guard.DidNotReceive().IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }
}
