using Shouldly;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Handlers.Clients;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Events;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Application.DTOs.Staffs;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Commands.Clients;

[TestFixture]
public class PutCommandHandlerTests
{
    private IClientRepository _clientRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private ClientMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IGroupVisibilityService _groupVisibilityService = null!;
    private IGroupVisibilityGuard _groupVisibilityGuard = null!;
    private IEmailClientAssignmentService _emailClientAssignmentService = null!;
    private IDomainEventDispatcher _eventDispatcher = null!;
    private IUserService _userService = null!;
    private ILogger<PutCommandHandler> _logger = null!;
    private PutCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        _mapper = new ClientMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _groupVisibilityService = Substitute.For<IGroupVisibilityService>();
        _groupVisibilityGuard = Substitute.For<IGroupVisibilityGuard>();
        _emailClientAssignmentService = Substitute.For<IEmailClientAssignmentService>();
        _eventDispatcher = Substitute.For<IDomainEventDispatcher>();
        _userService = Substitute.For<IUserService>();

        // The default caller is the Planer floor: every authenticated user holds it, and it carries
        // CanViewContracts but not CanEditContracts, so the contract branch still refuses by default.
        _userService.GetRights().Returns(Permissions.PlannerFloor);
        _logger = Substitute.For<ILogger<PutCommandHandler>>();

        _handler = new PutCommandHandler(
            _clientRepository,
            _clientVisibilityGuard,
            _mapper,
            _unitOfWork,
            _groupVisibilityService,
            _groupVisibilityGuard,
            _emailClientAssignmentService,
            _eventDispatcher,
            _userService,
            _logger
        );
    }

    [Test]
    public async Task Handle_ClientOutsideTheCallersVisibility_IsRefusedLikeAMissingClient()
    {
        var clientId = Guid.NewGuid();
        _clientVisibilityGuard.IsVisibleAsync(clientId, Arg.Any<CancellationToken>()).Returns(false);
        _clientRepository.GetTrackedForUpdate(clientId)
            .Returns(Task.FromResult<Client?>(CreateTestClient(clientId, "Hidden Client")));
        var resource = _mapper.ToResource(CreateTestClient(clientId, "Overwritten"));

        Func<Task> act = async () => await _handler.Handle(new PutCommand<ClientResource>(resource), CancellationToken.None);

        var exception = await act.ShouldThrowAsync<KeyNotFoundException>();
        exception.Message.ShouldBe($"Client with ID {clientId} not found");
        await _clientRepository.DidNotReceive().GetTrackedForUpdate(clientId);
        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>(), Arg.Any<Client>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
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
        _clientRepository.GetTrackedForUpdate(clientId).Returns(Task.FromResult<Client?>(existingClient));
        _clientRepository.Put(Arg.Any<Client>(), Arg.Any<Client>()).Returns(Task.FromResult<Client?>(existingClient));

        var command = new PutCommand<ClientResource>(updatedResource);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.ShouldNotBeNull();
        await _clientRepository.Received(1).Put(Arg.Any<Client>(), Arg.Any<Client>());
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
        _clientRepository.GetTrackedForUpdate(clientId).Returns(Task.FromResult<Client?>(existingClient));

        var command = new PutCommand<ClientResource>(updatedResource);

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        (await Should.ThrowAsync<InvalidRequestException>(act)).Message.ShouldContain("requires the right to edit contracts");

        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>(), Arg.Any<Client>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    /// <summary>
    /// The owner decision of 21.09.2026: assigning, re-dating and detaching a person's contract is
    /// supervisor work. The supervisor is not an admin, so the old IsAdmin() gate refused them — and the
    /// Ui offered the form all the same, which made the card a dead end for the role that is supposed to
    /// use it.
    /// </summary>
    [Test]
    public async Task Handle_NonAdminHoldingCanEditContracts_CanModifyClientContracts()
    {
        var clientId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var existingClient = CreateTestClient(clientId, "Test Client");
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
                    FromDate = new DateOnly(2024, 6, 1),
                    UntilDate = new DateOnly(2024, 12, 31)
                }
            },
            GroupItems = new List<ClientGroupItemResource>()
        };

        _groupVisibilityService.IsAdmin().Returns(Task.FromResult(false));
        _userService.GetRights().Returns(Permissions.GetPermissionsForRole(Roles.Authorised));
        _clientRepository.GetTrackedForUpdate(clientId).Returns(Task.FromResult<Client?>(existingClient));
        _clientRepository.Put(Arg.Any<Client>(), Arg.Any<Client>()).Returns(Task.FromResult<Client?>(existingClient));

        var result = await _handler.Handle(new PutCommand<ClientResource>(updatedResource), CancellationToken.None);

        result.ShouldNotBeNull();
        await _clientRepository.Received(1).Put(Arg.Any<Client>(), Arg.Any<Client>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    /// <summary>
    /// A supervisor changing contracts in the same request may change memberships only within visible groups,
    /// so the widened contract gate cannot be used to pull a client into a hidden group.
    /// </summary>
    [Test]
    public async Task Handle_NonAdminHoldingCanEditContracts_CannotAddAHiddenGroup()
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
        _userService.GetRights().Returns(Permissions.GetPermissionsForRole(Roles.Authorised));
        _clientRepository.GetTrackedForUpdate(clientId).Returns(Task.FromResult<Client?>(existingClient));

        Func<Task> act = async () => await _handler.Handle(
            new PutCommand<ClientResource>(updatedResource), CancellationToken.None);

        (await Should.ThrowAsync<KeyNotFoundException>(act)).Message.ShouldStartWith("Group with ID ");

        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>(), Arg.Any<Client>());
    }

    // Owner decision 2026-10-01: editing a client follows the same rule as creating one and as the GroupItems
    // endpoints — a non-admin may change memberships within the groups they can see.
    [Test]
    public async Task Handle_NonAdminUser_CanReplaceVisibleGroups()
    {
        var clientId = Guid.NewGuid();
        var oldGroupId = Guid.NewGuid();
        var newGroupId = Guid.NewGuid();
        var existingClient = CreateTestClient(clientId, "Test Client");
        existingClient.GroupItems = new List<GroupItem>
        {
            new GroupItem { GroupId = oldGroupId, ClientId = clientId, ValidFrom = new DateTime(2024, 1, 1) }
        };
        var updatedResource = new ClientResource
        {
            Id = clientId,
            Name = "Test Client",
            ClientContracts = new List<ClientContractResource>(),
            GroupItems = new List<ClientGroupItemResource>
            {
                new ClientGroupItemResource { GroupId = newGroupId, ClientId = clientId, ValidFrom = new DateTime(2024, 6, 1) }
            }
        };
        _groupVisibilityService.IsAdmin().Returns(Task.FromResult(false));
        _groupVisibilityGuard
            .AreAllGroupsVisibleAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Count == 2 && ids.Contains(oldGroupId) && ids.Contains(newGroupId)),
                Arg.Any<CancellationToken>())
            .Returns(true);
        _clientRepository.GetTrackedForUpdate(clientId).Returns(Task.FromResult<Client?>(existingClient));
        _clientRepository.Put(Arg.Any<Client>(), Arg.Any<Client>()).Returns(Task.FromResult<Client?>(existingClient));

        var result = await _handler.Handle(new PutCommand<ClientResource>(updatedResource), CancellationToken.None);

        result.ShouldNotBeNull();
        await _clientRepository.Received(1).Put(Arg.Any<Client>(), Arg.Any<Client>());
    }

    [Test]
    public async Task Handle_NonAdminUser_CannotRemoveAHiddenGroup()
    {
        var clientId = Guid.NewGuid();
        var hiddenGroupId = Guid.NewGuid();
        var existingClient = CreateTestClient(clientId, "Test Client");
        existingClient.GroupItems = new List<GroupItem>
        {
            new GroupItem { GroupId = hiddenGroupId, ClientId = clientId, ValidFrom = new DateTime(2024, 1, 1) }
        };
        var updatedResource = new ClientResource
        {
            Id = clientId,
            Name = "Test Client",
            ClientContracts = new List<ClientContractResource>(),
            GroupItems = new List<ClientGroupItemResource>()
        };
        _groupVisibilityService.IsAdmin().Returns(Task.FromResult(false));
        _clientRepository.GetTrackedForUpdate(clientId).Returns(Task.FromResult<Client?>(existingClient));

        Func<Task> act = async () => await _handler.Handle(new PutCommand<ClientResource>(updatedResource), CancellationToken.None);

        (await Should.ThrowAsync<KeyNotFoundException>(act)).Message.ShouldBe($"Group with ID {hiddenGroupId} not found");
        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>(), Arg.Any<Client>());
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
        _clientRepository.GetTrackedForUpdate(clientId).Returns(Task.FromResult<Client?>(existingClient));
        _clientRepository.Put(Arg.Any<Client>(), Arg.Any<Client>()).Returns(Task.FromResult<Client?>(existingClient));

        var command = new PutCommand<ClientResource>(updatedResource);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.ShouldNotBeNull();
        await _clientRepository.Received(1).Put(Arg.Any<Client>(), Arg.Any<Client>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handle_NonAdminUser_CannotRedateAHiddenGroup()
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
        _clientRepository.GetTrackedForUpdate(clientId).Returns(Task.FromResult<Client?>(existingClient));

        var command = new PutCommand<ClientResource>(updatedResource);

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        (await Should.ThrowAsync<KeyNotFoundException>(act)).Message.ShouldStartWith("Group with ID ");

        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>(), Arg.Any<Client>());
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
        _clientRepository.GetTrackedForUpdate(clientId).Returns(Task.FromResult<Client?>(existingClient));
        _clientRepository.Put(Arg.Any<Client>(), Arg.Any<Client>()).Returns(Task.FromResult<Client?>(existingClient));

        var command = new PutCommand<ClientResource>(updatedResource);

        var result = await _handler.Handle(command, CancellationToken.None);

        result.ShouldNotBeNull();
        await _clientRepository.Received(1).Put(Arg.Any<Client>(), Arg.Any<Client>());
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
        _clientRepository.GetTrackedForUpdate(clientId).Returns(Task.FromResult<Client?>(existingClient));

        var command = new PutCommand<ClientResource>(updatedResource);

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        (await Should.ThrowAsync<InvalidRequestException>(act)).Message.ShouldContain("requires the right to edit contracts");

        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>(), Arg.Any<Client>());
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
        _clientRepository.GetTrackedForUpdate(clientId).Returns(Task.FromResult<Client?>(existingClient));

        var command = new PutCommand<ClientResource>(updatedResource);

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        (await Should.ThrowAsync<InvalidRequestException>(act)).Message.ShouldContain("requires the right to edit contracts");

        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>(), Arg.Any<Client>());
    }

    [Test]
    public async Task Handle_NonAdminUser_CannotAddAHiddenGroup()
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
        _clientRepository.GetTrackedForUpdate(clientId).Returns(Task.FromResult<Client?>(existingClient));

        var command = new PutCommand<ClientResource>(updatedResource);

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        (await Should.ThrowAsync<KeyNotFoundException>(act)).Message.ShouldStartWith("Group with ID ");

        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>(), Arg.Any<Client>());
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
        _clientRepository.GetTrackedForUpdate(clientId).Returns(Task.FromResult<Client?>(null));

        var command = new PutCommand<ClientResource>(updatedResource);

        Func<Task> act = async () => await _handler.Handle(command, CancellationToken.None);

        await act.ShouldThrowAsync<KeyNotFoundException>();

        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>(), Arg.Any<Client>());
    }

    [Test]
    public async Task Handle_AdminChangesClientContract_DispatchesContractChangedEventWithEarliestFromDate()
    {
        var clientId = Guid.NewGuid();
        var clientContractId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var existingClient = CreateTestClient(clientId, "Test Client");
        existingClient.ClientContracts = new List<ClientContract>
        {
            new ClientContract
            {
                Id = clientContractId,
                ContractId = contractId,
                IsActive = true,
                FromDate = new DateOnly(2026, 3, 1),
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
                    Id = clientContractId,
                    ContractId = contractId,
                    IsActive = true,
                    FromDate = new DateOnly(2026, 1, 1),
                    UntilDate = null
                }
            },
            GroupItems = new List<ClientGroupItemResource>()
        };

        _groupVisibilityService.IsAdmin().Returns(Task.FromResult(true));
        _clientRepository.GetTrackedForUpdate(clientId).Returns(Task.FromResult<Client?>(existingClient));
        _clientRepository.Put(Arg.Any<Client>(), Arg.Any<Client>()).Returns(Task.FromResult<Client?>(existingClient));

        await _handler.Handle(new PutCommand<ClientResource>(updatedResource), CancellationToken.None);

        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Is<IDomainEvent>(e =>
                e is ContractChangedEvent &&
                ((ContractChangedEvent)e).ContractId == contractId &&
                ((ContractChangedEvent)e).ClientId == clientId &&
                ((ContractChangedEvent)e).RecalculationFrom == new DateOnly(2026, 1, 1)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_ClientContractsUnchanged_DoesNotDispatchContractChangedEvent()
    {
        var clientId = Guid.NewGuid();
        var clientContractId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var existingClient = CreateTestClient(clientId, "Old Name");
        existingClient.ClientContracts = new List<ClientContract>
        {
            new ClientContract
            {
                Id = clientContractId,
                ContractId = contractId,
                IsActive = true,
                FromDate = new DateOnly(2026, 3, 1),
                UntilDate = null
            }
        };

        var updatedResource = new ClientResource
        {
            Id = clientId,
            Name = "New Name",
            ClientContracts = new List<ClientContractResource>
            {
                new ClientContractResource
                {
                    Id = clientContractId,
                    ContractId = contractId,
                    IsActive = true,
                    FromDate = new DateOnly(2026, 3, 1),
                    UntilDate = null
                }
            },
            GroupItems = new List<ClientGroupItemResource>()
        };

        _groupVisibilityService.IsAdmin().Returns(Task.FromResult(true));
        _clientRepository.GetTrackedForUpdate(clientId).Returns(Task.FromResult<Client?>(existingClient));
        _clientRepository.Put(Arg.Any<Client>(), Arg.Any<Client>()).Returns(Task.FromResult<Client?>(existingClient));

        await _handler.Handle(new PutCommand<ClientResource>(updatedResource), CancellationToken.None);

        await _eventDispatcher.DidNotReceive().DispatchAsync(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Handle_AdminRemovesClientContract_DispatchesContractChangedEventForRemovedContract()
    {
        var clientId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var existingClient = CreateTestClient(clientId, "Test Client");
        existingClient.ClientContracts = new List<ClientContract>
        {
            new ClientContract
            {
                Id = Guid.NewGuid(),
                ContractId = contractId,
                IsActive = true,
                FromDate = new DateOnly(2026, 2, 1),
                UntilDate = null
            }
        };

        var updatedResource = new ClientResource
        {
            Id = clientId,
            Name = "Test Client",
            ClientContracts = new List<ClientContractResource>(),
            GroupItems = new List<ClientGroupItemResource>()
        };

        _groupVisibilityService.IsAdmin().Returns(Task.FromResult(true));
        _clientRepository.GetTrackedForUpdate(clientId).Returns(Task.FromResult<Client?>(existingClient));
        _clientRepository.Put(Arg.Any<Client>(), Arg.Any<Client>()).Returns(Task.FromResult<Client?>(existingClient));

        await _handler.Handle(new PutCommand<ClientResource>(updatedResource), CancellationToken.None);

        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Is<IDomainEvent>(e =>
                e is ContractChangedEvent &&
                ((ContractChangedEvent)e).ContractId == contractId &&
                ((ContractChangedEvent)e).RecalculationFrom == new DateOnly(2026, 2, 1)),
            Arg.Any<CancellationToken>());
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
