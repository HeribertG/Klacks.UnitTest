// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Address reads and writes must respect the group visibility of the owning client: an address of a hidden
/// client is answered exactly like a missing address, the address lists of a hidden client are empty, and
/// no write touches the repository or commits when the stored or the requested owner is hidden.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Queries.Addresses;
using Microsoft.Extensions.Logging;
using AddressDeleteHandler = Klacks.Api.Application.Handlers.Addresses.DeleteCommandHandler;
using AddressGetHandler = Klacks.Api.Application.Handlers.Addresses.GetQueryHandler;
using AddressPostHandler = Klacks.Api.Application.Handlers.Addresses.PostCommandHandler;
using AddressPutHandler = Klacks.Api.Application.Handlers.Addresses.PutCommandHandler;
using ClientAddressListHandler = Klacks.Api.Application.Handlers.Addresses.GetClientAddressListQueryHandler;
using SimpleAddressListHandler = Klacks.Api.Application.Handlers.Addresses.GetSimpleAddressListQueryHandler;

namespace Klacks.UnitTest.Application.Handlers.Addresses;

[TestFixture]
public class AddressHandlersVisibilityTests
{
    private IAddressRepository _addressRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IUnitOfWork _unitOfWork = null!;
    private AddressCommunicationMapper _mapper = null!;

    [SetUp]
    public void SetUp()
    {
        _addressRepository = Substitute.For<IAddressRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _mapper = new AddressCommunicationMapper();
    }

    private static Address AddressOf(Guid clientId) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        Street = "Bahnhofstrasse 1",
        Zip = "3000",
        City = "Bern"
    };

    private static AddressResource ResourceOf(Guid id, Guid clientId) => new()
    {
        Id = id,
        ClientId = clientId,
        Street = "Bahnhofstrasse 1",
        Zip = "3000",
        City = "Bern"
    };

    private void ClientIsVisible(Guid clientId, bool visible)
    {
        _clientVisibilityGuard.IsVisibleAsync(clientId, Arg.Any<CancellationToken>()).Returns(visible);
    }

    private async Task NothingWasWrittenAsync()
    {
        await _addressRepository.DidNotReceive().Add(Arg.Any<Address>());
        await _addressRepository.DidNotReceive().Put(Arg.Any<Address>());
        await _addressRepository.DidNotReceive().Delete(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Get_AddressOfVisibleClient_IsReturned()
    {
        var clientId = Guid.NewGuid();
        var address = AddressOf(clientId);
        _addressRepository.Get(address.Id).Returns(address);
        ClientIsVisible(clientId, true);
        var handler = new AddressGetHandler(_addressRepository, _clientVisibilityGuard, _mapper, Substitute.For<ILogger<AddressGetHandler>>());

        var result = await handler.Handle(new GetQuery<AddressResource>(address.Id), CancellationToken.None);

        result.Id.ShouldBe(address.Id);
    }

    [Test]
    public async Task Get_AddressOfHiddenClient_IsAnsweredLikeAMissingAddress()
    {
        var hiddenClientId = Guid.NewGuid();
        var address = AddressOf(hiddenClientId);
        var missingId = Guid.NewGuid();
        _addressRepository.Get(address.Id).Returns(address);
        ClientIsVisible(hiddenClientId, false);
        var handler = new AddressGetHandler(_addressRepository, _clientVisibilityGuard, _mapper, Substitute.For<ILogger<AddressGetHandler>>());

        var hidden = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<AddressResource>(address.Id), CancellationToken.None));
        var missing = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new GetQuery<AddressResource>(missingId), CancellationToken.None));

        hidden.Message.ShouldBe($"Address with ID {address.Id} not found");
        missing.Message.ShouldBe($"Address with ID {missingId} not found");
    }

    [Test]
    public async Task ClientAddressList_OfHiddenClient_IsEmptyAndDoesNotQueryTheRepository()
    {
        var hiddenClientId = Guid.NewGuid();
        ClientIsVisible(hiddenClientId, false);
        _addressRepository.ClienList(hiddenClientId).Returns(new List<Address> { AddressOf(hiddenClientId) });
        var handler = new ClientAddressListHandler(_addressRepository, _clientVisibilityGuard, _mapper, Substitute.For<ILogger<ClientAddressListHandler>>());

        var result = await handler.Handle(new ClientAddressListQuery(hiddenClientId), CancellationToken.None);

        result.ShouldBeEmpty();
        await _addressRepository.DidNotReceive().ClienList(Arg.Any<Guid>());
    }

    [Test]
    public async Task ClientAddressList_OfVisibleClient_ReturnsItsAddresses()
    {
        var clientId = Guid.NewGuid();
        ClientIsVisible(clientId, true);
        _addressRepository.ClienList(clientId).Returns(new List<Address> { AddressOf(clientId) });
        var handler = new ClientAddressListHandler(_addressRepository, _clientVisibilityGuard, _mapper, Substitute.For<ILogger<ClientAddressListHandler>>());

        var result = await handler.Handle(new ClientAddressListQuery(clientId), CancellationToken.None);

        result.Count().ShouldBe(1);
    }

    [Test]
    public async Task SimpleAddressList_OfHiddenClient_IsEmptyAndDoesNotQueryTheRepository()
    {
        var hiddenClientId = Guid.NewGuid();
        ClientIsVisible(hiddenClientId, false);
        _addressRepository.SimpleList(hiddenClientId).Returns(new List<Address> { AddressOf(hiddenClientId) });
        var handler = new SimpleAddressListHandler(_addressRepository, _clientVisibilityGuard, _mapper, Substitute.For<ILogger<SimpleAddressListHandler>>());

        var result = await handler.Handle(new GetSimpleAddressListQuery(hiddenClientId), CancellationToken.None);

        result.ShouldBeEmpty();
        await _addressRepository.DidNotReceive().SimpleList(Arg.Any<Guid>());
    }

    [Test]
    public async Task Post_ForHiddenClient_IsRefusedLikeAMissingClientAndWritesNothing()
    {
        var hiddenClientId = Guid.NewGuid();
        ClientIsVisible(hiddenClientId, false);
        var handler = new AddressPostHandler(_addressRepository, _clientVisibilityGuard, _mapper, _unitOfWork, Substitute.For<ILogger<AddressPostHandler>>());

        var exception = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new PostCommand<AddressResource>(ResourceOf(Guid.Empty, hiddenClientId)), CancellationToken.None));

        exception.Message.ShouldBe($"Client with ID {hiddenClientId} not found");
        await NothingWasWrittenAsync();
    }

    [Test]
    public async Task Post_ForVisibleClient_IsWritten()
    {
        var clientId = Guid.NewGuid();
        ClientIsVisible(clientId, true);
        var handler = new AddressPostHandler(_addressRepository, _clientVisibilityGuard, _mapper, _unitOfWork, Substitute.For<ILogger<AddressPostHandler>>());

        await handler.Handle(new PostCommand<AddressResource>(ResourceOf(Guid.Empty, clientId)), CancellationToken.None);

        await _addressRepository.Received(1).Add(Arg.Any<Address>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Put_AddressWhoseStoredOwnerIsHidden_IsRefusedLikeAMissingAddress()
    {
        var hiddenClientId = Guid.NewGuid();
        var visibleClientId = Guid.NewGuid();
        var stored = AddressOf(hiddenClientId);
        _addressRepository.GetNoTracking(stored.Id).Returns(stored);
        _clientVisibilityGuard
            .AreAllVisibleAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(hiddenClientId)), Arg.Any<CancellationToken>())
            .Returns(false);
        var handler = new AddressPutHandler(_addressRepository, _clientVisibilityGuard, _mapper, _unitOfWork, Substitute.For<ILogger<AddressPutHandler>>());

        var exception = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new PutCommand<AddressResource>(ResourceOf(stored.Id, visibleClientId)), CancellationToken.None));

        exception.Message.ShouldBe($"Address with ID {stored.Id} not found.");
        await NothingWasWrittenAsync();
    }

    [Test]
    public async Task Put_MovingAnAddressOntoAHiddenClient_IsRefusedLikeAMissingAddress()
    {
        var visibleClientId = Guid.NewGuid();
        var hiddenClientId = Guid.NewGuid();
        var stored = AddressOf(visibleClientId);
        _addressRepository.GetNoTracking(stored.Id).Returns(stored);
        _clientVisibilityGuard
            .AreAllVisibleAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(hiddenClientId)), Arg.Any<CancellationToken>())
            .Returns(false);
        var handler = new AddressPutHandler(_addressRepository, _clientVisibilityGuard, _mapper, _unitOfWork, Substitute.For<ILogger<AddressPutHandler>>());

        var exception = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new PutCommand<AddressResource>(ResourceOf(stored.Id, hiddenClientId)), CancellationToken.None));

        exception.Message.ShouldBe($"Address with ID {stored.Id} not found.");
        await NothingWasWrittenAsync();
    }

    [Test]
    public async Task Put_WithinVisibleClient_IsWritten()
    {
        var clientId = Guid.NewGuid();
        var stored = AddressOf(clientId);
        _addressRepository.GetNoTracking(stored.Id).Returns(stored);
        _clientVisibilityGuard
            .AreAllVisibleAsync(Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.All(id => id == clientId)), Arg.Any<CancellationToken>())
            .Returns(true);
        var handler = new AddressPutHandler(_addressRepository, _clientVisibilityGuard, _mapper, _unitOfWork, Substitute.For<ILogger<AddressPutHandler>>());

        await handler.Handle(new PutCommand<AddressResource>(ResourceOf(stored.Id, clientId)), CancellationToken.None);

        await _addressRepository.Received(1).Put(Arg.Any<Address>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Delete_AddressOfHiddenClient_IsRefusedLikeAMissingAddress()
    {
        var hiddenClientId = Guid.NewGuid();
        var stored = AddressOf(hiddenClientId);
        _addressRepository.Get(stored.Id).Returns(stored);
        ClientIsVisible(hiddenClientId, false);
        var handler = new AddressDeleteHandler(_addressRepository, _clientVisibilityGuard, _mapper, _unitOfWork, Substitute.For<ILogger<AddressDeleteHandler>>());

        var exception = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(new DeleteCommand<AddressResource>(stored.Id), CancellationToken.None));

        exception.Message.ShouldBe($"Address with ID {stored.Id} not found.");
        await NothingWasWrittenAsync();
    }
}
