using FluentAssertions;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Filters;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace UnitTest.Repository;

[TestFixture]
public class WorkRepositorySortingTests
{
    private DataBaseContext _context = null!;
    private IWorkRepository _workRepository = null!;
    private IClientGroupFilterService _mockGroupFilterService = null!;
    private IClientSearchFilterService _mockSearchFilterService = null!;
    private IWorkMacroService _mockWorkMacroService = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);

        _mockGroupFilterService = Substitute.For<IClientGroupFilterService>();
        _mockSearchFilterService = Substitute.For<IClientSearchFilterService>();
        _mockWorkMacroService = Substitute.For<IWorkMacroService>();

        _mockGroupFilterService.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));
        _mockSearchFilterService.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Client>)args[0]);

        var mockLogger = Substitute.For<ILogger<Klacks.Api.Domain.Models.Schedules.Work>>();

        _workRepository = new WorkRepository(
            _context,
            mockLogger,
            _mockGroupFilterService,
            _mockSearchFilterService,
            _mockWorkMacroService);

        await CreateTestDataWithContracts();
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }

    private async Task CreateTestDataWithContracts()
    {
        var contractLow = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Low Hours",
            GuaranteedHours = 80
        };
        var contractMedium = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "Medium Hours",
            GuaranteedHours = 120
        };
        var contractHigh = new Contract
        {
            Id = Guid.NewGuid(),
            Name = "High Hours",
            GuaranteedHours = 160
        };

        _context.Contract.AddRange(contractLow, contractMedium, contractHigh);
        await _context.SaveChangesAsync();

        var today = DateTime.Now;
        var refDate = new DateOnly(today.Year, today.Month, 1);

        var clientAliceLowHours = new Client
        {
            Id = Guid.NewGuid(),
            Name = "Anderson",
            FirstName = "Alice",
            IdNumber = 100,
            Type = EntityTypeEnum.Employee,
            Gender = GenderEnum.Female,
            LegalEntity = false,
            Membership = new Membership
            {
                ValidFrom = today.AddYears(-1),
                ValidUntil = today.AddYears(1)
            },
            ClientContracts = new List<ClientContract>
            {
                new ClientContract
                {
                    Id = Guid.NewGuid(),
                    ContractId = contractLow.Id,
                    Contract = contractLow,
                    FromDate = refDate.AddMonths(-6),
                    UntilDate = null
                }
            }
        };

        var clientBobHighHours = new Client
        {
            Id = Guid.NewGuid(),
            Name = "Brown",
            FirstName = "Bob",
            IdNumber = 200,
            Type = EntityTypeEnum.Employee,
            Gender = GenderEnum.Male,
            LegalEntity = false,
            Membership = new Membership
            {
                ValidFrom = today.AddYears(-1),
                ValidUntil = today.AddYears(1)
            },
            ClientContracts = new List<ClientContract>
            {
                new ClientContract
                {
                    Id = Guid.NewGuid(),
                    ContractId = contractHigh.Id,
                    Contract = contractHigh,
                    FromDate = refDate.AddMonths(-6),
                    UntilDate = null
                }
            }
        };

        var clientCharlieExtern = new Client
        {
            Id = Guid.NewGuid(),
            Name = "Clark",
            FirstName = "Charlie",
            IdNumber = 300,
            Type = EntityTypeEnum.ExternEmp,
            Gender = GenderEnum.Male,
            LegalEntity = false,
            Membership = new Membership
            {
                ValidFrom = today.AddYears(-1),
                ValidUntil = today.AddYears(1)
            },
            ClientContracts = new List<ClientContract>
            {
                new ClientContract
                {
                    Id = Guid.NewGuid(),
                    ContractId = contractMedium.Id,
                    Contract = contractMedium,
                    FromDate = refDate.AddMonths(-6),
                    UntilDate = null
                }
            }
        };

        var clientDianaMediumHours = new Client
        {
            Id = Guid.NewGuid(),
            Name = "Davis",
            FirstName = "Diana",
            IdNumber = 400,
            Type = EntityTypeEnum.Employee,
            Gender = GenderEnum.Female,
            LegalEntity = false,
            Membership = new Membership
            {
                ValidFrom = today.AddYears(-1),
                ValidUntil = today.AddYears(1)
            },
            ClientContracts = new List<ClientContract>
            {
                new ClientContract
                {
                    Id = Guid.NewGuid(),
                    ContractId = contractMedium.Id,
                    Contract = contractMedium,
                    FromDate = refDate.AddMonths(-6),
                    UntilDate = null
                }
            }
        };

        _context.Client.AddRange(clientAliceLowHours, clientBobHighHours, clientCharlieExtern, clientDianaMediumHours);
        await _context.SaveChangesAsync();
    }

    [Test]
    public async Task WorkList_WithShowEmployeesOnly_ShouldFilterExtern()
    {
        // Arrange
        var today = DateTime.Now;
        var startDate = new DateOnly(today.Year, today.Month, 1).AddDays(-5);
        var endDate = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)).AddDays(5);
        var filter = new WorkFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            ShowEmployees = true,
            ShowExtern = false,
            OrderBy = "name",
            SortOrder = "asc"
        };

        // Act
        var result = await _workRepository.WorkList(filter);

        // Assert
        result.Clients.Should().HaveCount(3);
        result.Clients.Should().OnlyContain(c => c.Type == EntityTypeEnum.Employee);
        result.Clients.Should().NotContain(c => c.FirstName == "Charlie");
    }

    [Test]
    public async Task WorkList_WithShowExternOnly_ShouldFilterEmployees()
    {
        // Arrange
        var today = DateTime.Now;
        var startDate = new DateOnly(today.Year, today.Month, 1).AddDays(-5);
        var endDate = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)).AddDays(5);
        var filter = new WorkFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            ShowEmployees = false,
            ShowExtern = true,
            OrderBy = "name",
            SortOrder = "asc"
        };

        // Act
        var result = await _workRepository.WorkList(filter);

        // Assert
        result.Clients.Should().HaveCount(1);
        result.Clients.Should().OnlyContain(c => c.Type == EntityTypeEnum.ExternEmp);
        result.Clients[0].FirstName.Should().Be("Charlie");
    }

    [Test]
    public async Task WorkList_WithBothTypesDisabled_ShouldReturnEmpty()
    {
        // Arrange
        var today = DateTime.Now;
        var startDate = new DateOnly(today.Year, today.Month, 1).AddDays(-5);
        var endDate = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)).AddDays(5);
        var filter = new WorkFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            ShowEmployees = false,
            ShowExtern = false,
            OrderBy = "name",
            SortOrder = "asc"
        };

        // Act
        var result = await _workRepository.WorkList(filter);

        // Assert
        result.Clients.Should().BeEmpty();
    }

    [Test]
    public async Task WorkList_WithNameAsc_ShouldSortByNameAscending()
    {
        // Arrange
        var today = DateTime.Now;
        var startDate = new DateOnly(today.Year, today.Month, 1).AddDays(-5);
        var endDate = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)).AddDays(5);
        var filter = new WorkFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            ShowEmployees = true,
            ShowExtern = true,
            OrderBy = "name",
            SortOrder = "asc"
        };

        // Act
        var result = await _workRepository.WorkList(filter);

        // Assert
        result.Clients.Should().HaveCount(4);
        result.Clients[0].Name.Should().Be("Anderson");
        result.Clients[1].Name.Should().Be("Brown");
        result.Clients[2].Name.Should().Be("Clark");
        result.Clients[3].Name.Should().Be("Davis");
    }

    [Test]
    public async Task WorkList_WithNameDesc_ShouldSortByNameDescending()
    {
        // Arrange
        var today = DateTime.Now;
        var startDate = new DateOnly(today.Year, today.Month, 1).AddDays(-5);
        var endDate = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)).AddDays(5);
        var filter = new WorkFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            ShowEmployees = true,
            ShowExtern = true,
            OrderBy = "name",
            SortOrder = "desc"
        };

        // Act
        var result = await _workRepository.WorkList(filter);

        // Assert
        result.Clients.Should().HaveCount(4);
        result.Clients[0].Name.Should().Be("Davis");
        result.Clients[1].Name.Should().Be("Clark");
        result.Clients[2].Name.Should().Be("Brown");
        result.Clients[3].Name.Should().Be("Anderson");
    }

    [Test]
    public async Task WorkList_WithNameAscAndHoursAsc_ShouldUseThenByForHours()
    {
        // Arrange
        var today = DateTime.Now;
        var startDate = new DateOnly(today.Year, today.Month, 1).AddDays(-5);
        var endDate = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)).AddDays(5);
        var filter = new WorkFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            ShowEmployees = true,
            ShowExtern = true,
            OrderBy = "name",
            SortOrder = "asc",
            HoursSortOrder = "asc"
        };

        // Act
        var result = await _workRepository.WorkList(filter);

        // Assert
        result.Clients.Should().HaveCount(4);
        result.Clients[0].Name.Should().Be("Anderson");
        result.Clients[1].Name.Should().Be("Brown");
        result.Clients[2].Name.Should().Be("Clark");
        result.Clients[3].Name.Should().Be("Davis");
    }

    [Test]
    public async Task WorkList_WithNameAscAndHoursDesc_ShouldUseThenByDescendingForHours()
    {
        // Arrange
        var today = DateTime.Now;
        var startDate = new DateOnly(today.Year, today.Month, 1).AddDays(-5);
        var endDate = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)).AddDays(5);
        var filter = new WorkFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            ShowEmployees = true,
            ShowExtern = true,
            OrderBy = "name",
            SortOrder = "asc",
            HoursSortOrder = "desc"
        };

        // Act
        var result = await _workRepository.WorkList(filter);

        // Assert
        result.Clients.Should().HaveCount(4);
        result.Clients[0].Name.Should().Be("Anderson");
        result.Clients[1].Name.Should().Be("Brown");
        result.Clients[2].Name.Should().Be("Clark");
        result.Clients[3].Name.Should().Be("Davis");
    }

    [Test]
    public async Task WorkList_WithFirstNameSort_ShouldSortByFirstName()
    {
        // Arrange
        var today = DateTime.Now;
        var startDate = new DateOnly(today.Year, today.Month, 1).AddDays(-5);
        var endDate = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)).AddDays(5);
        var filter = new WorkFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            ShowEmployees = true,
            ShowExtern = true,
            OrderBy = "firstName",
            SortOrder = "asc"
        };

        // Act
        var result = await _workRepository.WorkList(filter);

        // Assert
        result.Clients.Should().HaveCount(4);
        result.Clients[0].FirstName.Should().Be("Alice");
        result.Clients[1].FirstName.Should().Be("Bob");
        result.Clients[2].FirstName.Should().Be("Charlie");
        result.Clients[3].FirstName.Should().Be("Diana");
    }

    [Test]
    public async Task WorkList_HoursSortOrderIsIndependentFromPrimarySort()
    {
        // Arrange
        var today = DateTime.Now;
        var startDate = new DateOnly(today.Year, today.Month, 1).AddDays(-5);
        var endDate = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)).AddDays(5);
        var filterWithHours = new WorkFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            ShowEmployees = true,
            ShowExtern = true,
            OrderBy = "firstName",
            SortOrder = "desc",
            HoursSortOrder = "asc"
        };

        var filterWithoutHours = new WorkFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            ShowEmployees = true,
            ShowExtern = true,
            OrderBy = "firstName",
            SortOrder = "desc",
            HoursSortOrder = null
        };

        // Act
        var resultWithHours = await _workRepository.WorkList(filterWithHours);
        var resultWithoutHours = await _workRepository.WorkList(filterWithoutHours);

        // Assert
        resultWithHours.Clients.Should().HaveCount(4);
        resultWithoutHours.Clients.Should().HaveCount(4);
        resultWithHours.Clients[0].FirstName.Should().Be("Diana");
        resultWithoutHours.Clients[0].FirstName.Should().Be("Diana");
    }
}
