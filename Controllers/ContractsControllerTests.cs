using FluentAssertions;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.Queries;
using Klacks.Api.Presentation.Controllers.UserBackend.Associations;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class ContractsControllerTests
{
    private IMediator mockMediator;
    private ILogger<ContractsController> mockLogger;
    private ContractsController controller;

    [SetUp]
    public void Setup()
    {
        mockMediator = Substitute.For<IMediator>();
        mockLogger = Substitute.For<ILogger<ContractsController>>();
        controller = new ContractsController(mockMediator, mockLogger);
    }

    [Test]
    public async Task GetContracts_ShouldReturnAllContracts()
    {
        // Arrange
        var expectedContracts = new List<ContractResource>
        {
            new ContractResource
            {
                Id = Guid.NewGuid(),
                Name = "Contract 1",
                GuaranteedHours = 160,
                MaximumHours = 200,
                MinimumHours = 120,
                ValidFrom = DateTime.UtcNow,
                CalendarSelection = new CalendarSelectionResource { Id = Guid.NewGuid(), Name = "Calendar 1" }
            },
            new ContractResource
            {
                Id = Guid.NewGuid(),
                Name = "Contract 2",
                GuaranteedHours = 100,
                MaximumHours = 150,
                MinimumHours = 80,
                ValidFrom = DateTime.UtcNow,
                CalendarSelection = new CalendarSelectionResource { Id = Guid.NewGuid(), Name = "Calendar 2" }
            }
        };

        mockMediator.Send(Arg.Any<ListQuery<ContractResource>>())
            .Returns(expectedContracts);

        // Act
        var result = await controller.GetContracts();

        // Assert
        result.Should().BeOfType<ActionResult<IEnumerable<ContractResource>>>();
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var contracts = okResult.Value.Should().BeAssignableTo<IEnumerable<ContractResource>>().Subject;
        contracts.Should().HaveCount(2);
        contracts.Should().BeEquivalentTo(expectedContracts);
    }

    [Test]
    public async Task GetContracts_WhenExceptionOccurs_ShouldThrow()
    {
        // Arrange
        var expectedException = new Exception("Database error");
        mockMediator.Send(Arg.Any<ListQuery<ContractResource>>())
            .Throws(expectedException);

        // Act & Assert
        var act = async () => await controller.GetContracts();
        await act.Should().ThrowAsync<Exception>()
            .WithMessage("Database error");
    }

    [Test]
    public async Task Get_ShouldReturnContractById()
    {
        // Arrange
        var contractId = Guid.NewGuid();
        var expectedContract = new ContractResource
        {
            Id = contractId,
            Name = "Test Contract",
            GuaranteedHours = 160,
            MaximumHours = 200,
            MinimumHours = 120,
            ValidFrom = DateTime.UtcNow,
            CalendarSelection = new CalendarSelectionResource { Id = Guid.NewGuid(), Name = "Test Calendar" }
        };

        mockMediator.Send(Arg.Any<GetQuery<ContractResource>>())
            .Returns(expectedContract);

        // Act
        var result = await controller.Get(contractId);

        // Assert
        result.Should().BeOfType<ActionResult<ContractResource>>();
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var contract = okResult.Value.Should().BeOfType<ContractResource>().Subject;
        contract.Should().BeEquivalentTo(expectedContract);
    }

    [Test]
    public async Task Post_ShouldCreateNewContract()
    {
        // Arrange
        var newContract = new ContractResource
        {
            Name = "New Contract",
            GuaranteedHours = 160,
            MaximumHours = 200,
            MinimumHours = 120,
            ValidFrom = DateTime.UtcNow,
            CalendarSelection = new CalendarSelectionResource { Id = Guid.NewGuid() }
        };

        var createdContract = new ContractResource
        {
            Id = Guid.NewGuid(),
            Name = newContract.Name,
            GuaranteedHours = newContract.GuaranteedHours,
            MaximumHours = newContract.MaximumHours,
            MinimumHours = newContract.MinimumHours,
            ValidFrom = newContract.ValidFrom,
            CalendarSelection = newContract.CalendarSelection
        };

        mockMediator.Send(Arg.Any<PostCommand<ContractResource>>())
            .Returns(createdContract);

        // Act
        var result = await controller.Post(newContract);

        // Assert
        result.Should().BeOfType<ActionResult<ContractResource>>();
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var contract = okResult.Value.Should().BeOfType<ContractResource>().Subject;
        contract.Should().BeEquivalentTo(createdContract);
    }

    [Test]
    public async Task Put_ShouldUpdateExistingContract()
    {
        // Arrange
        var updateContract = new ContractResource
        {
            Id = Guid.NewGuid(),
            Name = "Updated Contract",
            GuaranteedHours = 180,
            MaximumHours = 220,
            MinimumHours = 140,
            ValidFrom = DateTime.UtcNow,
            CalendarSelection = new CalendarSelectionResource { Id = Guid.NewGuid() }
        };

        mockMediator.Send(Arg.Any<PutCommand<ContractResource>>())
            .Returns(updateContract);

        // Act
        var result = await controller.Put(updateContract);

        // Assert
        result.Should().BeOfType<ActionResult<ContractResource>>();
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var contract = okResult.Value.Should().BeOfType<ContractResource>().Subject;
        contract.Should().BeEquivalentTo(updateContract);
    }

    [Test]
    public async Task Delete_ShouldDeleteContract()
    {
        // Arrange
        var contractId = Guid.NewGuid();
        var deletedContract = new ContractResource
        {
            Id = contractId,
            Name = "Deleted Contract"
        };

        mockMediator.Send(Arg.Any<DeleteCommand<ContractResource>>())
            .Returns(deletedContract);

        // Act
        var result = await controller.Delete(contractId);

        // Assert
        result.Should().BeOfType<ActionResult<ContractResource>>();
        var okResult = result.Result.Should().BeOfType<OkObjectResult>().Subject;
        var contract = okResult.Value.Should().BeOfType<ContractResource>().Subject;
        contract.Id.Should().Be(contractId);
    }

    [Test]
    public async Task GetContracts_ShouldLogInformation()
    {
        // Arrange
        var contracts = new List<ContractResource>
        {
            new ContractResource { Id = Guid.NewGuid(), Name = "Contract 1" },
            new ContractResource { Id = Guid.NewGuid(), Name = "Contract 2" }
        };

        mockMediator.Send(Arg.Any<ListQuery<ContractResource>>())
            .Returns(contracts);

        // Act
        await controller.GetContracts();

        // Assert
        mockLogger.Received().LogInformation("Fetching all contracts.");
        mockLogger.Received().LogInformation($"Retrieved {contracts.Count()} contracts.");
    }

    [Test]
    public async Task GetContracts_WhenExceptionOccurs_ShouldPropagateException()
    {
        // Arrange
        var exception = new Exception("Test error");
        mockMediator.Send(Arg.Any<ListQuery<ContractResource>>())
            .Throws(exception);

        // Act & Assert
        Assert.ThrowsAsync<Exception>(() => controller.GetContracts());
        
        // Verify that information logging still occurs before exception
        mockLogger.Received().LogInformation("Fetching all contracts.");
    }
}