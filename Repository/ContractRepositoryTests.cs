using FluentAssertions;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.CalendarSelections;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class ContractRepositoryTests
{
    private DataBaseContext dbContext;
    private ILogger<Contract> mockLogger;
    private ContractRepository contractRepository;
    private CalendarSelection testCalendarSelection;
    private Membership testMembership;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        dbContext = new DataBaseContext(options, httpContextAccessor);
        
        mockLogger = Substitute.For<ILogger<Contract>>();
        contractRepository = new ContractRepository(dbContext, mockLogger);

        // Setup test data
        testCalendarSelection = new CalendarSelection
        {
            Id = Guid.NewGuid(),
            Name = "Test Calendar"
        };

        testMembership = new Membership
        {
            Id = Guid.NewGuid(),
            Type = 1,
            ValidFrom = DateTime.UtcNow,
            ClientId = Guid.NewGuid()
        };

        dbContext.CalendarSelection.Add(testCalendarSelection);
        dbContext.Membership.Add(testMembership);
        dbContext.SaveChanges();
    }

    [TearDown]
    public void TearDown()
    {
        dbContext?.Dispose();
    }

    [Test]
    public async Task Add_ShouldAddContractToDatabase()
    {
        // Arrange
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Test Contract",
            GuaranteedHours = 160,
            MaximumHours = 200,
            MinimumHours = 120,
            ValidFrom = DateTime.UtcNow,
            ValidUntil = DateTime.UtcNow.AddYears(1),
            CalendarSelectionId = testCalendarSelection.Id
        };

        // Act
        await contractRepository.Add(contract);
        await dbContext.SaveChangesAsync();

        // Assert
        var savedContract = await dbContext.Contract.FirstOrDefaultAsync(c => c.Id == contract.Id);
        savedContract.Should().NotBeNull();
        savedContract.Name.Should().Be("Test Contract");
        savedContract.GuaranteedHours.Should().Be(160);
        savedContract.MaximumHours.Should().Be(200);
        savedContract.MinimumHours.Should().Be(120);
    }

    [Test]
    public async Task Get_ShouldReturnContractWithIncludes()
    {
        // Arrange
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Test Contract",
            GuaranteedHours = 160,
            MaximumHours = 200,
            MinimumHours = 120,
            ValidFrom = DateTime.UtcNow,
            CalendarSelectionId = testCalendarSelection.Id
        };

        dbContext.Contract.Add(contract);
        await dbContext.SaveChangesAsync();

        // Act
        var result = await contractRepository.Get(contract.Id);

        // Assert
        result.Should().NotBeNull();
        result.Id.Should().Be(contract.Id);
        result.Name.Should().Be("Test Contract");
        result.CalendarSelection.Should().NotBeNull();
        result.CalendarSelection.Name.Should().Be("Test Calendar");
    }

    [Test]
    public async Task List_ShouldReturnAllContracts()
    {
        // Arrange
        var contracts = new List<Contract>
        {
            new Contract
            {
                Id = Guid.NewGuid(),
                Name = "Contract 1",
                GuaranteedHours = 160,
                MaximumHours = 200,
                MinimumHours = 120,
                ValidFrom = DateTime.UtcNow,
                CalendarSelectionId = testCalendarSelection.Id
            },
            new Contract
            {
                Id = Guid.NewGuid(),
                Name = "Contract 2",
                GuaranteedHours = 100,
                MaximumHours = 150,
                MinimumHours = 80,
                ValidFrom = DateTime.UtcNow,
                CalendarSelectionId = testCalendarSelection.Id
            }
        };

        dbContext.Contract.AddRange(contracts);
        await dbContext.SaveChangesAsync();

        // Act
        var result = await contractRepository.List();

        // Assert
        result.Should().NotBeNull();
        result.Should().HaveCount(2);
        result.Should().Contain(c => c.Name == "Contract 1");
        result.Should().Contain(c => c.Name == "Contract 2");
    }

    [Test]
    public async Task Put_ShouldUpdateContract()
    {
        // Arrange
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Original Contract",
            GuaranteedHours = 160,
            MaximumHours = 200,
            MinimumHours = 120,
            ValidFrom = DateTime.UtcNow,
            CalendarSelectionId = testCalendarSelection.Id
        };

        dbContext.Contract.Add(contract);
        await dbContext.SaveChangesAsync();

        // Act
        contract.Name = "Updated Contract";
        contract.GuaranteedHours = 180;
        await contractRepository.Put(contract);
        await dbContext.SaveChangesAsync();

        // Assert
        var updatedContract = await dbContext.Contract.FirstOrDefaultAsync(c => c.Id == contract.Id);
        updatedContract.Should().NotBeNull();
        updatedContract.Name.Should().Be("Updated Contract");
        updatedContract.GuaranteedHours.Should().Be(180);
    }

    [Test]
    public async Task Delete_ShouldSoftDeleteContract()
    {
        // Arrange
        var contract = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Contract to Delete",
            GuaranteedHours = 160,
            MaximumHours = 200,
            MinimumHours = 120,
            ValidFrom = DateTime.UtcNow,
            CalendarSelectionId = testCalendarSelection.Id
        };

        dbContext.Contract.Add(contract);
        await dbContext.SaveChangesAsync();

        // Act
        var deletedContract = await contractRepository.Delete(contract.Id);
        await dbContext.SaveChangesAsync();

        // Assert
        deletedContract.Should().NotBeNull();
        deletedContract.Id.Should().Be(contract.Id);

        // Verify soft delete
        var contractInDb = await dbContext.Contract
            .IgnoreQueryFilters()
            .FirstOrDefaultAsync(c => c.Id == contract.Id);
        
        contractInDb.Should().NotBeNull();
        contractInDb.IsDeleted.Should().BeTrue();
    }

    [Test]
    public async Task Get_WithNonExistentId_ShouldReturnNull()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act
        var result = await contractRepository.Get(nonExistentId);

        // Assert
        result.Should().BeNull();
    }

    [Test]
    public async Task Delete_WithNonExistentId_ShouldThrowException()
    {
        // Arrange
        var nonExistentId = Guid.NewGuid();

        // Act & Assert
        var act = async () => await contractRepository.Delete(nonExistentId);
        await act.Should().ThrowAsync<ArgumentNullException>();
    }
}