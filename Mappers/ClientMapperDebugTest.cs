using FluentAssertions;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Presentation.DTOs.Associations;
using Klacks.Api.Presentation.DTOs.Staffs;

namespace Klacks.UnitTest.Mappers;

[TestFixture]
public class ClientMapperDebugTest
{
    private ClientMapper _mapper = null!;

    [SetUp]
    public void Setup()
    {
        _mapper = new ClientMapper();
    }

    [Test]
    public void ToEntity_WithMinimalResource_Succeeds()
    {
        // Arrange
        var resource = new ClientResource
        {
            Id = Guid.NewGuid(),
            Name = "Test"
        };

        // Act
        var entity = _mapper.ToEntity(resource);

        // Assert
        entity.Should().NotBeNull();
        entity.Name.Should().Be("Test");
    }

    [Test]
    public void ToResource_WithMinimalClient_Succeeds()
    {
        // Arrange
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = "Test",
            Addresses = new List<Address>(),
            Communications = new List<Communication>(),
            Annotations = new List<Annotation>(),
            Works = new List<Work>(),
            ClientContracts = new List<ClientContract>(),
            GroupItems = new List<GroupItem>()
        };

        // Act
        var resource = _mapper.ToResource(client);

        // Assert
        resource.Should().NotBeNull();
        resource.Name.Should().Be("Test");
    }

    [Test]
    public void ToResource_WithDefaultClient_Succeeds()
    {
        // Arrange
        var client = new Client
        {
            Id = Guid.NewGuid(),
            Name = "Test"
        };

        // Act
        var resource = _mapper.ToResource(client);

        // Assert
        resource.Should().NotBeNull();
        resource.Name.Should().Be("Test");
    }

    [Test]
    public void SimulatePutCommandHandler_ToEntityThenToResource_Succeeds()
    {
        // Arrange
        var clientId = Guid.NewGuid();
        var inputResource = new ClientResource
        {
            Id = clientId,
            Name = "Test Client",
            ClientContracts = new List<ClientContractResource>(),
            GroupItems = new List<ClientGroupItemResource>()
        };

        var existingClient = new Client
        {
            Id = clientId,
            Name = "Test Client",
            Addresses = new List<Address>(),
            Communications = new List<Communication>(),
            Annotations = new List<Annotation>(),
            Works = new List<Work>(),
            ClientContracts = new List<ClientContract>(),
            GroupItems = new List<GroupItem>()
        };

        // Act - This simulates what PutCommandHandler does
        var mappedEntity = _mapper.ToEntity(inputResource);
        var resultResource = _mapper.ToResource(existingClient);

        // Assert
        mappedEntity.Should().NotBeNull();
        mappedEntity.Name.Should().Be("Test Client");
        resultResource.Should().NotBeNull();
        resultResource.Name.Should().Be("Test Client");
    }

    [Test]
    public void ToEntity_WithClientContracts_MapsCorrectly()
    {
        // Arrange
        var resource = new ClientResource
        {
            Id = Guid.NewGuid(),
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

        // Act
        var entity = _mapper.ToEntity(resource);

        // Assert
        entity.Should().NotBeNull();
        entity.ClientContracts.Should().HaveCount(1);
    }

    [Test]
    public void FullHandlerSimulation_WithClientContracts_Succeeds()
    {
        // Arrange - This exactly mirrors the PutCommandHandlerTests.Handle_AdminUser_CanModifyClientContracts test
        var clientId = Guid.NewGuid();

        var existingClient = new Client
        {
            Id = clientId,
            Name = "Test Client",
            Addresses = new List<Address>(),
            Communications = new List<Communication>(),
            Annotations = new List<Annotation>(),
            Works = new List<Work>(),
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

        // Act - Simulates exactly what PutCommandHandler does
        // Step 1: Log info about received resource (skipped - no logger)
        // Step 2: Check admin (skipped - assume true)
        // Step 3: Map resource to entity
        var client = _mapper.ToEntity(updatedResource);
        // Step 4: Call repository Put (simulated - returns existingClient)
        var updatedClient = existingClient;
        // Step 5: Map entity back to resource
        var result = _mapper.ToResource(updatedClient);

        // Assert
        client.Should().NotBeNull();
        result.Should().NotBeNull();
        result.Name.Should().Be("Test Client");
    }
}
