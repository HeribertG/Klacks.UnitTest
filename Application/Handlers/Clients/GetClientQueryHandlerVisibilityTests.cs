// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// GET api/backend/Clients/{id} is reachable by every authenticated caller. Until 2026-10-01 its handler
/// read the client without consulting group visibility, so a group-restricted user received the complete
/// record (addresses, communications, contracts, notes) of any client whose id they knew. A hidden client
/// must now be answered exactly like a missing one.
/// </summary>

using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Handlers.Clients;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Domain.Models.Staffs;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Clients;

[TestFixture]
public class GetClientQueryHandlerVisibilityTests
{
    private IClientRepository _clientRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private GetQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _handler = new GetQueryHandler(
            _clientRepository,
            _clientVisibilityGuard,
            new ClientMapper(),
            Substitute.For<ILogger<GetQueryHandler>>());
    }

    [Test]
    public async Task VisibleClient_IsReturned()
    {
        var clientId = Guid.NewGuid();
        _clientVisibilityGuard.IsVisibleAsync(clientId, Arg.Any<CancellationToken>()).Returns(true);
        _clientRepository.Get(clientId).Returns(new Client { Id = clientId, Name = "Steiner" });

        var result = await _handler.Handle(new GetQuery<ClientResource>(clientId), CancellationToken.None);

        result.Id.ShouldBe(clientId);
    }

    [Test]
    public async Task ClientOutsideTheCallersVisibility_IsAnsweredLikeAMissingClient()
    {
        var hiddenId = Guid.NewGuid();
        var missingId = Guid.NewGuid();
        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(false);
        _clientRepository.Get(hiddenId).Returns(new Client { Id = hiddenId, Name = "Steinmann" });

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new GetQuery<ClientResource>(hiddenId), CancellationToken.None));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(
            () => _handler.Handle(new GetQuery<ClientResource>(missingId), CancellationToken.None));

        hidden.Message.ShouldBe($"Client with ID {hiddenId} not found");
        missing.Message.ShouldBe($"Client with ID {missingId} not found");
        await _clientRepository.DidNotReceive().Get(hiddenId);
    }
}
