using Shouldly;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Filters;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Repository;

[TestFixture]
public class WorkRepositorySortingTests
{
    private DataBaseContext _context = null!;
    private IWorkRepository _workRepository = null!;
    private IWorkMacroService _mockWorkMacroService = null!;

    [SetUp]
    public async Task SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        var mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, mockHttpContextAccessor);

        _mockWorkMacroService = Substitute.For<IWorkMacroService>();

        var mockGroupFilterService = Substitute.For<IClientGroupFilterService>();
        mockGroupFilterService.FilterClientsByGroupId(Arg.Any<Guid?>(), Arg.Any<IQueryable<Client>>())
            .Returns(args => Task.FromResult((IQueryable<Client>)args[1]));
        var mockSearchFilterService = Substitute.For<IClientSearchFilterService>();
        mockSearchFilterService.ApplySearchFilter(Arg.Any<IQueryable<Client>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Client>)args[0]);

        var baseQueryService = new ClientBaseQueryService(_context, mockGroupFilterService, mockSearchFilterService);

        var mockLogger = Substitute.For<ILogger<Klacks.Api.Domain.Models.Schedules.Work>>();

        var mockContractDataProvider = Substitute.For<Klacks.Api.Domain.Interfaces.Associations.IClientContractDataProvider>();
        _workRepository = new WorkRepository(
            _context,
            mockLogger,
            baseQueryService,
            _mockWorkMacroService,
            mockContractDataProvider);

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
        result.Clients.Count().ShouldBe(3);
        result.Clients.ShouldAllBe(c => c.Type == EntityTypeEnum.Employee);
        result.Clients.ShouldNotContain(c => c.FirstName == "Charlie");
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
        result.Clients.Count().ShouldBe(1);
        result.Clients.ShouldAllBe(c => c.Type == EntityTypeEnum.ExternEmp);
        result.Clients[0].FirstName.ShouldBe("Charlie");
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
        result.Clients.ShouldBeEmpty();
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
        result.Clients.Count().ShouldBe(4);
        result.Clients[0].Name.ShouldBe("Anderson");
        result.Clients[1].Name.ShouldBe("Brown");
        result.Clients[2].Name.ShouldBe("Clark");
        result.Clients[3].Name.ShouldBe("Davis");
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
        result.Clients.Count().ShouldBe(4);
        result.Clients[0].Name.ShouldBe("Davis");
        result.Clients[1].Name.ShouldBe("Clark");
        result.Clients[2].Name.ShouldBe("Brown");
        result.Clients[3].Name.ShouldBe("Anderson");
    }

    [Test]
    public async Task WorkList_WithGuaranteedHoursAsc_ShouldSortByGuaranteedHoursAscending()
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
            OrderBy = "guaranteedhours",
            SortOrder = "asc"
        };

        // Act
        var result = await _workRepository.WorkList(filter);

        // Assert
        result.Clients.Count().ShouldBe(4);
    }

    [Test]
    public async Task WorkList_WithGuaranteedHoursDesc_ShouldSortByGuaranteedHoursDescending()
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
            OrderBy = "guaranteedhours",
            SortOrder = "desc"
        };

        // Act
        var result = await _workRepository.WorkList(filter);

        // Assert
        result.Clients.Count().ShouldBe(4);
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
        result.Clients.Count().ShouldBe(4);
        result.Clients[0].FirstName.ShouldBe("Alice");
        result.Clients[1].FirstName.ShouldBe("Bob");
        result.Clients[2].FirstName.ShouldBe("Charlie");
        result.Clients[3].FirstName.ShouldBe("Diana");
    }

    [Test]
    public async Task WorkList_WithIndividualSort_ShouldSortByNameThenFirstName()
    {
        // Arrange
        var today = DateTime.Now;
        var startDate = new DateOnly(today.Year, today.Month, 1).AddDays(-5);
        var endDate = new DateOnly(today.Year, today.Month, DateTime.DaysInMonth(today.Year, today.Month)).AddDays(5);
        var filterWithIndividual = new WorkFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            ShowEmployees = true,
            ShowExtern = true,
            OrderBy = "firstName",
            SortOrder = "desc",
            IndividualSort = true
        };

        var filterWithoutIndividual = new WorkFilter
        {
            StartDate = startDate,
            EndDate = endDate,
            ShowEmployees = true,
            ShowExtern = true,
            OrderBy = "firstName",
            SortOrder = "desc",
            IndividualSort = false
        };

        // Act
        var resultWithIndividual = await _workRepository.WorkList(filterWithIndividual);
        var resultWithoutIndividual = await _workRepository.WorkList(filterWithoutIndividual);

        // Assert
        resultWithIndividual.Clients.Count().ShouldBe(4);
        resultWithoutIndividual.Clients.Count().ShouldBe(4);
        resultWithIndividual.Clients[0].Name.ShouldBe("Anderson");
        resultWithoutIndividual.Clients[0].FirstName.ShouldBe("Diana");
    }
}
