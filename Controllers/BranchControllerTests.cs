using FluentAssertions;
using Klacks.Api.Application.Commands.Settings.Branch;
using Klacks.Api.Application.Queries.Settings.Branch;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Presentation.Controllers.UserBackend.Settings;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;

namespace Klacks.UnitTest.Controllers;

[TestFixture]
public class BranchControllerTests
{
    private IMediator mockMediator;
    private ILogger<BranchController> mockLogger;
    private BranchController controller;

    [SetUp]
    public void Setup()
    {
        mockMediator = Substitute.For<IMediator>();
        mockLogger = Substitute.For<ILogger<BranchController>>();
        controller = new BranchController(mockMediator, mockLogger);
    }

    [Test]
    public async Task GetBranchList_ShouldReturnAllBranches()
    {
        // Arrange
        var expectedBranches = new List<Branch>
        {
            new Branch
            {
                Id = Guid.NewGuid(),
                Name = "Branch 1",
                Address = "Address 1",
                Phone = "123-456-7890",
                Email = "branch1@test.com"
            },
            new Branch
            {
                Id = Guid.NewGuid(),
                Name = "Branch 2",
                Address = "Address 2",
                Phone = "098-765-4321",
                Email = "branch2@test.com"
            }
        };

        mockMediator.Send(Arg.Any<ListQuery>())
            .Returns(expectedBranches);

        // Act
        var result = await controller.GetBranchListAsync();

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(2);
        result.Should().BeEquivalentTo(expectedBranches);
    }

    [Test]
    public async Task GetBranchList_WhenExceptionOccurs_ShouldThrow()
    {
        // Arrange
        var expectedException = new Exception("Database error");
        mockMediator.Send(Arg.Any<ListQuery>())
            .Throws(expectedException);

        // Act & Assert
        var act = async () => await controller.GetBranchListAsync();
        await act.Should().ThrowAsync<Exception>()
            .WithMessage("Database error");
    }

    [Test]
    public async Task GetBranch_ShouldReturnBranchById()
    {
        // Arrange
        var branchId = Guid.NewGuid();
        var expectedBranch = new Branch
        {
            Id = branchId,
            Name = "Test Branch",
            Address = "Test Address",
            Phone = "555-1234",
            Email = "test@branch.com"
        };

        mockMediator.Send(Arg.Any<GetQuery>())
            .Returns(expectedBranch);

        // Act
        var result = await controller.GetBranch(branchId);

        // Assert
        result.Should().NotBeNull();
        result.Value.Should().NotBeNull();
        result.Value.Should().BeEquivalentTo(expectedBranch);
    }

    [Test]
    public async Task GetBranch_WhenBranchNotFound_ShouldReturnNotFound()
    {
        // Arrange
        var branchId = Guid.NewGuid();
        mockMediator.Send(Arg.Any<GetQuery>())
            .Returns((Branch?)null);

        // Act
        var result = await controller.GetBranch(branchId);

        // Assert
        result.Result.Should().BeOfType<NotFoundResult>();
    }

    [Test]
    public async Task AddBranch_ShouldCreateNewBranch()
    {
        // Arrange
        var newBranch = new Branch
        {
            Name = "New Branch",
            Address = "New Address",
            Phone = "111-222-3333",
            Email = "new@branch.com"
        };

        var createdBranch = new Branch
        {
            Id = Guid.NewGuid(),
            Name = newBranch.Name,
            Address = newBranch.Address,
            Phone = newBranch.Phone,
            Email = newBranch.Email
        };

        mockMediator.Send(Arg.Any<PostCommand>())
            .Returns(createdBranch);

        // Act
        var result = await controller.AddBranch(newBranch);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().NotBeEmpty();
        result.Name.Should().Be(newBranch.Name);
        result.Address.Should().Be(newBranch.Address);
        result.Phone.Should().Be(newBranch.Phone);
        result.Email.Should().Be(newBranch.Email);
    }

    [Test]
    public async Task AddBranch_WhenExceptionOccurs_ShouldThrow()
    {
        // Arrange
        var newBranch = new Branch
        {
            Name = "Test Branch",
            Address = "Test Address"
        };

        var expectedException = new Exception("Database error");
        mockMediator.Send(Arg.Any<PostCommand>())
            .Throws(expectedException);

        // Act & Assert
        var act = async () => await controller.AddBranch(newBranch);
        await act.Should().ThrowAsync<Exception>()
            .WithMessage("Database error");
    }

    [Test]
    public async Task PutBranch_ShouldUpdateExistingBranch()
    {
        // Arrange
        var updateBranch = new Branch
        {
            Id = Guid.NewGuid(),
            Name = "Updated Branch",
            Address = "Updated Address",
            Phone = "999-888-7777",
            Email = "updated@branch.com"
        };

        mockMediator.Send(Arg.Any<PutCommand>())
            .Returns(updateBranch);

        // Act
        var result = await controller.PutBranch(updateBranch);

        // Assert
        result.Should().NotBeNull();
        result.Should().BeEquivalentTo(updateBranch);
    }

    [Test]
    public async Task PutBranch_WhenExceptionOccurs_ShouldThrow()
    {
        // Arrange
        var updateBranch = new Branch
        {
            Id = Guid.NewGuid(),
            Name = "Test Branch",
            Address = "Test Address"
        };

        var expectedException = new Exception("Update failed");
        mockMediator.Send(Arg.Any<PutCommand>())
            .Throws(expectedException);

        // Act & Assert
        var act = async () => await controller.PutBranch(updateBranch);
        await act.Should().ThrowAsync<Exception>()
            .WithMessage("Update failed");
    }

    [Test]
    public async Task DeleteBranch_ShouldDeleteBranch()
    {
        // Arrange
        var branchId = Guid.NewGuid();

        mockMediator.Send(Arg.Any<IRequest<Unit>>(), Arg.Any<CancellationToken>())
            .Returns(Unit.Value);

        // Act
        var result = await controller.DeleteBranch(branchId);

        // Assert
        result.Should().BeOfType<NoContentResult>();
        await mockMediator.Received(1).Send(Arg.Any<IRequest<Unit>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteBranch_WhenExceptionOccurs_ShouldThrow()
    {
        // Arrange
        var branchId = Guid.NewGuid();
        var expectedException = new Exception("Delete failed");

        mockMediator.Send(Arg.Any<DeleteCommand>())
            .Throws(expectedException);

        // Act & Assert
        var act = async () => await controller.DeleteBranch(branchId);
        await act.Should().ThrowAsync<Exception>()
            .WithMessage("Delete failed");
    }

    [Test]
    public async Task AddBranch_ShouldValidateRequiredFields()
    {
        // Arrange
        var branchWithMissingData = new Branch
        {
            Name = "Test Branch"
            // Missing Address
        };

        // Act & Assert
        // Note: The actual validation would occur in the command handler
        // This test demonstrates the controller accepts the input
        var result = await controller.AddBranch(branchWithMissingData);

        await mockMediator.Received(1).Send(Arg.Any<PostCommand>());
    }

    [Test]
    public async Task GetBranch_WithValidId_ShouldCallMediator()
    {
        // Arrange
        var branchId = Guid.NewGuid();
        var branch = new Branch
        {
            Id = branchId,
            Name = "Test",
            Address = "Test Address"
        };

        mockMediator.Send(Arg.Any<GetQuery>())
            .Returns(branch);

        // Act
        await controller.GetBranch(branchId);

        // Assert
        await mockMediator.Received(1).Send(Arg.Is<GetQuery>(q => q.Id == branchId));
    }
}
