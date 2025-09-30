using AutoMapper;
using FluentAssertions;
using Klacks.Api.Application.AutoMapper;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.CalendarSelections;
using Klacks.Api.Presentation.DTOs.Associations;
using Klacks.Api.Presentation.DTOs.Schedules;
using NUnit.Framework;

namespace UnitTest.Mapping;

[TestFixture]
public class ContractMappingTests
{
    private IMapper mapper;

    [SetUp]
    public void Setup()
    {
        var config = new MapperConfiguration(cfg =>
        {
            cfg.AddMaps(typeof(ClientMappingProfile).Assembly);
        });
        mapper = config.CreateMapper();
    }

    [Test]
    public void MappingProfile_ShouldBeValid()
    {
        // Act & Assert
        var configuration = new MapperConfiguration(cfg => cfg.AddMaps(typeof(ClientMappingProfile).Assembly));
        configuration.AssertConfigurationIsValid();
    }

    [Test]
    public void Map_ContractToContractResource_ShouldMapAllProperties()
    {
        // Arrange
        var calendarSelection = new CalendarSelection
        {
            Id = Guid.NewGuid(),
            Name = "Test Calendar"
        };

        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Test Contract",
            GuaranteedHoursPerMonth = 160,
            MaximumHoursPerMonth = 200,
            MinimumHoursPerMonth = 120,
            ValidFrom = new DateTime(2024, 1, 1),
            ValidUntil = new DateTime(2024, 12, 31),
            CalendarSelectionId = calendarSelection.Id,
            CalendarSelection = calendarSelection,
            CreateTime = DateTime.UtcNow,
            CurrentUserCreated = "TestUser"
        };

        // Act
        var contractResource = mapper.Map<ContractResource>(contract);

        // Assert
        contractResource.Should().NotBeNull();
        contractResource.Id.Should().Be(contract.Id);
        contractResource.Name.Should().Be(contract.Name);
        contractResource.GuaranteedHoursPerMonth.Should().Be(contract.GuaranteedHoursPerMonth);
        contractResource.MaximumHoursPerMonth.Should().Be(contract.MaximumHoursPerMonth);
        contractResource.MinimumHoursPerMonth.Should().Be(contract.MinimumHoursPerMonth);
        contractResource.ValidFrom.Should().Be(contract.ValidFrom);
        contractResource.ValidUntil.Should().Be(contract.ValidUntil);
        contractResource.CalendarSelection.Should().NotBeNull();
        contractResource.CalendarSelection.Id.Should().Be(calendarSelection.Id);
        contractResource.CalendarSelection.Name.Should().Be(calendarSelection.Name);
    }

    [Test]
    public void Map_ContractResourceToContract_ShouldMapAllProperties()
    {
        // Arrange
        var calendarSelectionResource = new CalendarSelectionResource
        {
            Id = Guid.NewGuid(),
            Name = "Test Calendar"
        };

        var contractResource = new ContractResource
        {
            Id = Guid.NewGuid(),
            Name = "Test Contract",
            GuaranteedHoursPerMonth = 160,
            MaximumHoursPerMonth = 200,
            MinimumHoursPerMonth = 120,
            ValidFrom = new DateTime(2024, 1, 1),
            ValidUntil = new DateTime(2024, 12, 31),
            CalendarSelection = calendarSelectionResource
        };

        // Act
        var contract = mapper.Map<Contract>(contractResource);

        // Assert
        contract.Should().NotBeNull();
        contract.Id.Should().Be(contractResource.Id);
        contract.Name.Should().Be(contractResource.Name);
        contract.GuaranteedHoursPerMonth.Should().Be(contractResource.GuaranteedHoursPerMonth);
        contract.MaximumHoursPerMonth.Should().Be(contractResource.MaximumHoursPerMonth);
        contract.MinimumHoursPerMonth.Should().Be(contractResource.MinimumHoursPerMonth);
        contract.ValidFrom.Should().Be(contractResource.ValidFrom);
        contract.ValidUntil.Should().Be(contractResource.ValidUntil);
        
        // CalendarSelection navigation property should be ignored during mapping
        contract.CalendarSelection.Should().BeNull();
        
        // Base entity properties should be ignored
        contract.CreateTime.Should().BeNull();
        contract.CurrentUserCreated.Should().Be(string.Empty);
    }

    [Test]
    public void Map_ContractWithNullCalendarSelection_ShouldMapCorrectly()
    {
        // Arrange
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Test Contract",
            GuaranteedHoursPerMonth = 160,
            MaximumHoursPerMonth = 200,
            MinimumHoursPerMonth = 120,
            ValidFrom = DateTime.UtcNow,
            CalendarSelectionId = Guid.NewGuid(),
            CalendarSelection = null
        };

        // Act
        var contractResource = mapper.Map<ContractResource>(contract);

        // Assert
        contractResource.Should().NotBeNull();
        contractResource.CalendarSelection.Should().BeNull();
    }

    [Test]
    public void Map_ContractResourceWithNullCalendarSelection_ShouldMapCorrectly()
    {
        // Arrange
        var contractResource = new ContractResource
        {
            Id = Guid.NewGuid(),
            Name = "Test Contract",
            GuaranteedHoursPerMonth = 160,
            MaximumHoursPerMonth = 200,
            MinimumHoursPerMonth = 120,
            ValidFrom = DateTime.UtcNow,
            CalendarSelection = null
        };

        // Act
        var contract = mapper.Map<Contract>(contractResource);

        // Assert
        contract.Should().NotBeNull();
        contract.CalendarSelection.Should().BeNull();
    }

    [Test]
    public void Map_ContractWithNullValidUntil_ShouldMapCorrectly()
    {
        // Arrange
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Open-ended Contract",
            ValidFrom = DateTime.UtcNow,
            ValidUntil = null,
            CalendarSelectionId = Guid.NewGuid()
        };

        // Act
        var contractResource = mapper.Map<ContractResource>(contract);

        // Assert
        contractResource.Should().NotBeNull();
        contractResource.ValidUntil.Should().BeNull();
    }

    [Test]
    public void Map_ContractCollection_ShouldMapAllItems()
    {
        // Arrange
        var contracts = new List<Contract>
        {
            new Contract
            {
                Id = Guid.NewGuid(),
                Name = "Contract 1",
                GuaranteedHoursPerMonth = 160,
                ValidFrom = DateTime.UtcNow,
                CalendarSelectionId = Guid.NewGuid()
            },
            new Contract
            {
                Id = Guid.NewGuid(),
                Name = "Contract 2",
                GuaranteedHoursPerMonth = 120,
                ValidFrom = DateTime.UtcNow,
                CalendarSelectionId = Guid.NewGuid()
            }
        };

        // Act
        var contractResources = mapper.Map<List<ContractResource>>(contracts);

        // Assert
        contractResources.Should().NotBeNull();
        contractResources.Should().HaveCount(2);
        contractResources[0].Name.Should().Be("Contract 1");
        contractResources[1].Name.Should().Be("Contract 2");
    }
}