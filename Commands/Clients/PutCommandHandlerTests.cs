using FluentAssertions;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Handlers.Clients;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Presentation.DTOs.Staffs;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Commands.Clients;

[TestFixture]
public class PutCommandHandlerTests
{
    private IClientRepository _clientRepository = null!;
    private ClientMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IGroupVisibilityService _groupVisibilityService = null!;
    private ILogger<PutCommandHandler> _logger = null!;
    private PutCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _mapper = new ClientMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _groupVisibilityService = Substitute.For<IGroupVisibilityService>();
        _logger = Substitute.For<ILogger<PutCommandHandler>>();

        _handler = new PutCommandHandler(
            _clientRepository,
            _mapper,
            _unitOfWork,
            _groupVisibilityService,
            _logger
        );
    }

    [Test]
    public async Task Handle_AdminUser_CanModifyClientContracts()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var existingClient = CreateTestClient(clientId, "Test Client");
        existingClient.ClientContracts = new List<ClientContract>
        {
            new ClientContract
            {
                Id = Guid.NewGuid(),
                ContractId = Guid.NewGuid(),
                IsActive = true,
                FromDate = new DateOnly(2024, 1, 1),
                UntilDate = null
            }
        };

        var updatedResource = new ClientResource
        {
            Id = clientId,
            Name = "Test Client",
            ClientContracts = new List<ClientContractResource>
            {
                new ClientContractResource
                {
                    Id = Guid.NewGuid(),
                    ContractId = Guid.NewGuid(),
                    IsActive = false,
                    FromDate = new DateOnly(2024, 6, 1),
                    UntilDate = new DateOnly(2024, 12, 31)
                }
            },
            GroupItems = new List<ClientGroupItemResource>()
        };

        _groupVisibilityService.IsAdmin().Returns(Task.FromResult(true));
        _clientRepository.Get(clientId).Returns(Task.FromResult(existingClient));
        _clientRepository.Put(Arg.Any<Client>()).Returns(Task.FromResult(existingClient));

        var command = new PutCommand<ClientResource>(updatedResource);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Should().NotBeNull();
        await _clientRepository.Received(1).Put(Arg.Any<Client>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_NonAdminUser_CannotModifyClientContracts()
    {
        var clientId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var existingClient = new Client
        {
            Id = clientId,
            Name = "Test Client",
            ClientContracts = new List<ClientContract>
            {
                new ClientContract
                {
                    Id = contractId,
                    ContractId = Guid.NewGuid(),
                    IsActive = true,
                    FromDate = new DateOnly(2024, 1, 1),
                    UntilDate = null
                }
            },
            GroupItems = new List<GroupItem>()
        };

        var updatedResource = new ClientResource
        {
            Id = clientId,
            Name = "Test Client",
            ClientContracts = new List<ClientContractResource>
            {
                new ClientContractResource
                {
                    Id = contractId,
                    ContractId = Guid.NewGuid(),
                    IsActive = false,
                    FromDate = new DateOnly(2024, 1, 1),
                    UntilDate = null
                }
            },
            GroupItems = new List<ClientGroupItemResource>()
        };

        _groupVisibilityService.IsAdmin().Returns(Task.FromResult(false));
        _clientRepository.Get(clientId).Returns(Task.FromResult(existingClient));

        var command = new PutCommand<ClientResource>(updatedResource);

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>()
            .WithMessage("Only administrators can modify client contracts");

        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_AdminUser_CanModifyGroupItems()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var existingClient = CreateTestClient(clientId, "Test Client");
        existingClient.GroupItems = new List<GroupItem>
        {
            new GroupItem
            {
                GroupId = groupId,
                ClientId = clientId,
                ValidFrom = new DateTime(2024, 1, 1),
                ValidUntil = null
            }
        };

        var updatedResource = new ClientResource
        {
            Id = clientId,
            Name = "Test Client",
            ClientContracts = new List<ClientContractResource>(),
            GroupItems = new List<ClientGroupItemResource>
            {
                new ClientGroupItemResource
                {
                    GroupId = Guid.NewGuid(),
                    ClientId = clientId,
                    ValidFrom = new DateTime(2024, 6, 1),
                    ValidUntil = new DateTime(2024, 12, 31)
                }
            }
        };

        _groupVisibilityService.IsAdmin().Returns(Task.FromResult(true));
        _clientRepository.Get(clientId).Returns(Task.FromResult(existingClient));
        _clientRepository.Put(Arg.Any<Client>()).Returns(Task.FromResult(existingClient));

        var command = new PutCommand<ClientResource>(updatedResource);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Should().NotBeNull();
        await _clientRepository.Received(1).Put(Arg.Any<Client>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_NonAdminUser_CannotModifyGroupItems()
    {
        var clientId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var existingClient = new Client
        {
            Id = clientId,
            Name = "Test Client",
            ClientContracts = new List<ClientContract>(),
            GroupItems = new List<GroupItem>
            {
                new GroupItem
                {
                    GroupId = groupId,
                    ClientId = clientId,
                    ValidFrom = new DateTime(2024, 1, 1),
                    ValidUntil = null
                }
            }
        };

        var updatedResource = new ClientResource
        {
            Id = clientId,
            Name = "Test Client",
            ClientContracts = new List<ClientContractResource>(),
            GroupItems = new List<ClientGroupItemResource>
            {
                new ClientGroupItemResource
                {
                    GroupId = groupId,
                    ClientId = clientId,
                    ValidFrom = new DateTime(2024, 6, 1),
                    ValidUntil = null
                }
            }
        };

        _groupVisibilityService.IsAdmin().Returns(Task.FromResult(false));
        _clientRepository.Get(clientId).Returns(Task.FromResult(existingClient));

        var command = new PutCommand<ClientResource>(updatedResource);

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>()
            .WithMessage("Only administrators can modify client groups");

        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_NonAdminUser_CanModifyOtherFields()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var existingClient = CreateTestClient(clientId, "Old Name");
        existingClient.FirstName = "Old FirstName";
        existingClient.ClientContracts = new List<ClientContract>
        {
            new ClientContract
            {
                Id = contractId,
                ContractId = Guid.NewGuid(),
                IsActive = true,
                FromDate = new DateOnly(2024, 1, 1),
                UntilDate = null
            }
        };
        existingClient.GroupItems = new List<GroupItem>
        {
            new GroupItem
            {
                GroupId = groupId,
                ClientId = clientId,
                ValidFrom = new DateTime(2024, 1, 1),
                ValidUntil = null
            }
        };

        var updatedResource = new ClientResource
        {
            Id = clientId,
            Name = "New Name",
            FirstName = "New FirstName",
            ClientContracts = new List<ClientContractResource>
            {
                new ClientContractResource
                {
                    Id = contractId,
                    ContractId = existingClient.ClientContracts.First().ContractId,
                    IsActive = true,
                    FromDate = new DateOnly(2024, 1, 1),
                    UntilDate = null
                }
            },
            GroupItems = new List<ClientGroupItemResource>
            {
                new ClientGroupItemResource
                {
                    GroupId = groupId,
                    ClientId = clientId,
                    ValidFrom = new DateTime(2024, 1, 1),
                    ValidUntil = null
                }
            }
        };

        _groupVisibilityService.IsAdmin().Returns(Task.FromResult(false));
        _clientRepository.Get(clientId).Returns(Task.FromResult(existingClient));
        _clientRepository.Put(Arg.Any<Client>()).Returns(Task.FromResult(existingClient));

        var command = new PutCommand<ClientResource>(updatedResource);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.Should().NotBeNull();
        await _clientRepository.Received(1).Put(Arg.Any<Client>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_NonAdminUser_CannotAddNewContract()
    {
        var clientId = Guid.NewGuid();
        var existingClient = new Client
        {
            Id = clientId,
            Name = "Test Client",
            ClientContracts = new List<ClientContract>(),
            GroupItems = new List<GroupItem>()
        };

        var updatedResource = new ClientResource
        {
            Id = clientId,
            Name = "Test Client",
            ClientContracts = new List<ClientContractResource>
            {
                new ClientContractResource
                {
                    Id = Guid.NewGuid(),
                    ContractId = Guid.NewGuid(),
                    IsActive = true,
                    FromDate = new DateOnly(2024, 1, 1),
                    UntilDate = null
                }
            },
            GroupItems = new List<ClientGroupItemResource>()
        };

        _groupVisibilityService.IsAdmin().Returns(Task.FromResult(false));
        _clientRepository.Get(clientId).Returns(Task.FromResult(existingClient));

        var command = new PutCommand<ClientResource>(updatedResource);

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>()
            .WithMessage("Only administrators can modify client contracts");

        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>());
    }

    [Test]
    public async Task Handle_NonAdminUser_CannotRemoveContract()
    {
        var clientId = Guid.NewGuid();
        var existingClient = new Client
        {
            Id = clientId,
            Name = "Test Client",
            ClientContracts = new List<ClientContract>
            {
                new ClientContract
                {
                    Id = Guid.NewGuid(),
                    ContractId = Guid.NewGuid(),
                    IsActive = true,
                    FromDate = new DateOnly(2024, 1, 1),
                    UntilDate = null
                }
            },
            GroupItems = new List<GroupItem>()
        };

        var updatedResource = new ClientResource
        {
            Id = clientId,
            Name = "Test Client",
            ClientContracts = new List<ClientContractResource>(),
            GroupItems = new List<ClientGroupItemResource>()
        };

        _groupVisibilityService.IsAdmin().Returns(Task.FromResult(false));
        _clientRepository.Get(clientId).Returns(Task.FromResult(existingClient));

        var command = new PutCommand<ClientResource>(updatedResource);

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>()
            .WithMessage("Only administrators can modify client contracts");

        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>());
    }

    [Test]
    public async Task Handle_NonAdminUser_CannotAddNewGroupItem()
    {
        var clientId = Guid.NewGuid();
        var existingClient = new Client
        {
            Id = clientId,
            Name = "Test Client",
            ClientContracts = new List<ClientContract>(),
            GroupItems = new List<GroupItem>()
        };

        var updatedResource = new ClientResource
        {
            Id = clientId,
            Name = "Test Client",
            ClientContracts = new List<ClientContractResource>(),
            GroupItems = new List<ClientGroupItemResource>
            {
                new ClientGroupItemResource
                {
                    GroupId = Guid.NewGuid(),
                    ClientId = clientId,
                    ValidFrom = new DateTime(2024, 1, 1),
                    ValidUntil = null
                }
            }
        };

        _groupVisibilityService.IsAdmin().Returns(Task.FromResult(false));
        _clientRepository.Get(clientId).Returns(Task.FromResult(existingClient));

        var command = new PutCommand<ClientResource>(updatedResource);

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<InvalidRequestException>()
            .WithMessage("Only administrators can modify client groups");

        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>());
    }

    [Test]
    public async Task Handle_ClientNotFound_ThrowsKeyNotFoundException()
    {
        var clientId = Guid.NewGuid();
        var updatedResource = new ClientResource
        {
            Id = clientId,
            Name = "Test Client",
            ClientContracts = new List<ClientContractResource>(),
            GroupItems = new List<ClientGroupItemResource>()
        };

        _groupVisibilityService.IsAdmin().Returns(Task.FromResult(false));
        _clientRepository.Get(clientId).Returns(Task.FromResult<Client>(null!));

        var command = new PutCommand<ClientResource>(updatedResource);

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        await act.Should().ThrowAsync<KeyNotFoundException>();

        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>());
    }

    private static Client CreateTestClient(Guid clientId, string name)
    {
        return new Client
        {
            Id = clientId,
            Name = name,
            Addresses = new List<Address>(),
            Communications = new List<Communication>(),
            Annotations = new List<Annotation>(),
            Works = new List<Work>(),
            ClientContracts = new List<ClientContract>(),
            GroupItems = new List<GroupItem>()
        };
    }
}
