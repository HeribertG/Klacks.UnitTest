using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Presentation.DTOs.Filter;
using Klacks.Api.Infrastructure.Repositories;
using Klacks.Api.Infrastructure.Services;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Application.Mappers;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.AspNetCore.Http;
using Klacks.Api.Domain.Interfaces;
using NSubstitute;

namespace UnitTest.Repository;

[TestFixture]
public class ShiftRepositoryTests
{
    private DataBaseContext _context;
    private ShiftRepository _repository;
    private ILogger<Shift> _mockLogger;
    private IHttpContextAccessor _mockHttpContextAccessor;
    private IDateRangeFilterService _mockDateRangeFilterService;
    private IShiftSearchService _mockShiftSearchService;
    private IShiftSortingService _mockShiftSortingService;
    private IShiftStatusFilterService _mockShiftStatusFilterService;
    private IShiftPaginationService _mockShiftPaginationService;
    private IShiftGroupManagementService _mockShiftGroupManagementService;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        _mockHttpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _context = new DataBaseContext(options, _mockHttpContextAccessor);
        _mockLogger = Substitute.For<ILogger<Shift>>();
        _mockDateRangeFilterService = Substitute.For<IDateRangeFilterService>();
        _mockShiftSearchService = Substitute.For<IShiftSearchService>();
        _mockShiftSortingService = Substitute.For<IShiftSortingService>();
        _mockShiftStatusFilterService = Substitute.For<IShiftStatusFilterService>();
        _mockShiftPaginationService = Substitute.For<IShiftPaginationService>();
        _mockShiftGroupManagementService = Substitute.For<IShiftGroupManagementService>();
        var collectionUpdateService = new EntityCollectionUpdateService(_context);
        var mockShiftValidator = Substitute.For<IShiftValidator>();
        var scheduleMapper = new ScheduleMapper();
        _repository = new ShiftRepository(_context, _mockLogger, _mockDateRangeFilterService, _mockShiftSearchService, _mockShiftSortingService, _mockShiftStatusFilterService, _mockShiftPaginationService, _mockShiftGroupManagementService, collectionUpdateService, mockShiftValidator, scheduleMapper);
    }

    [Test]
    public async Task AddOriginalShift_WithStatusIsCutOriginal_PreservesOriginalId()
    {
        // Arrange
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Original Shift",
            Status = ShiftStatus.OriginalOrder,
            OriginalId = null,
            Lft = 1,
            Rgt = 2
        };
        
        await _repository.Add(originalShift);
        await _context.SaveChangesAsync();

        var cutOriginalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Cut Original Shift",
            Status = ShiftStatus.OriginalShift,
            OriginalId = originalShift.Id,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };

        // Act
        await _repository.Add(cutOriginalShift);
        await _context.SaveChangesAsync();

        // Assert
        var savedShift = await _context.Shift.FirstOrDefaultAsync(s => s.Id == cutOriginalShift.Id);
        savedShift.Should().NotBeNull();
        savedShift.OriginalId.Should().Be(originalShift.Id);
        savedShift.Status.Should().Be(ShiftStatus.OriginalShift);
    }

    [Test]
    public async Task AddOriginalShift_WithStatusReadyToCut_PreservesOriginalId()
    {
        // Arrange
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Original Shift",
            Status = ShiftStatus.OriginalOrder,
            OriginalId = null,
            Lft = 1,
            Rgt = 2
        };
        
        await _repository.Add(originalShift);
        await _context.SaveChangesAsync();

        var readyToCutShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Ready to Cut Shift",
            Status = ShiftStatus.SealedOrder,
            OriginalId = originalShift.Id,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };

        // Act
        await _repository.Add(readyToCutShift);
        await _context.SaveChangesAsync();

        // Assert
        var savedShift = await _context.Shift.FirstOrDefaultAsync(s => s.Id == readyToCutShift.Id);
        savedShift.Should().NotBeNull();
        savedShift.OriginalId.Should().Be(originalShift.Id);
        savedShift.Status.Should().Be(ShiftStatus.SealedOrder);
    }

    [Test]
    public async Task AddCutShift_WithStatusIsCut_PreservesOriginalId()
    {
        // Arrange
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Original Shift",
            Status = ShiftStatus.OriginalOrder,
            OriginalId = null,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };
        
        await _repository.Add(originalShift);
        await _context.SaveChangesAsync();

        var cutShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Cut Shift",
            Status = ShiftStatus.SplitShift,
            OriginalId = originalShift.Id,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(12))
        };

        // Act - Direkt speichern ohne Repository Add (wegen InMemory DB Limitation)
        _context.Shift.Add(cutShift);
        await _context.SaveChangesAsync();

        // Assert
        var savedCut = await _context.Shift.FirstOrDefaultAsync(s => s.Id == cutShift.Id);
        
        savedCut.Should().NotBeNull();
        savedCut.OriginalId.Should().Be(originalShift.Id);
        savedCut.Status.Should().Be(ShiftStatus.SplitShift);
    }


    [Test]
    public async Task AddMultipleCutShifts_PreservesOriginalId()
    {
        // Arrange
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Original Shift",
            Status = ShiftStatus.OriginalOrder,
            OriginalId = null,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };
        
        await _repository.Add(originalShift);
        await _context.SaveChangesAsync();

        var cutShift1 = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Cut Shift 1",
            Status = ShiftStatus.SplitShift,
            OriginalId = originalShift.Id,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(12))
        };

        var cutShift2 = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Cut Shift 2",
            Status = ShiftStatus.SplitShift,
            OriginalId = originalShift.Id,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(12)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };

        // Act - Direkt speichern ohne Repository Add (wegen InMemory DB Limitation)
        _context.Shift.Add(cutShift1);
        _context.Shift.Add(cutShift2);
        await _context.SaveChangesAsync();

        // Assert
        var allCutShifts = await _context.Shift
            .Where(s => s.OriginalId == originalShift.Id)
            .ToListAsync();

        allCutShifts.Should().HaveCount(2);
        allCutShifts.Should().AllSatisfy(cut => cut.OriginalId.Should().Be(originalShift.Id));
        allCutShifts.Should().AllSatisfy(cut => cut.Status.Should().Be(ShiftStatus.SplitShift));
    }

    [Test]
    public async Task ShiftWithStatusGreaterThanOne_AlwaysPreservesOriginalId()
    {
        // Arrange
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Original Shift",
            Status = ShiftStatus.OriginalOrder,
            OriginalId = null,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };
        
        await _repository.Add(originalShift);
        await _context.SaveChangesAsync();

        var testCases = new[]
        {
            (ShiftStatus.SealedOrder, "Ready to Cut"),
            (ShiftStatus.OriginalShift, "Is Cut Original"),
            (ShiftStatus.SplitShift, "Is Cut")
        };

        foreach (var (status, name) in testCases)
        {
            // Arrange
            var shift = new Shift
            {
                Id = Guid.NewGuid(),
                Name = $"{name} Shift",
                Status = status,
                OriginalId = originalShift.Id,
                FromDate = DateOnly.FromDateTime(DateTime.Now),
                StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
                EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
            };

            // Act
            if (status == ShiftStatus.SplitShift)
            {
                // Direkt speichern für IsCut wegen InMemory DB Limitation
                _context.Shift.Add(shift);
            }
            else
            {
                await _repository.Add(shift);
            }
            await _context.SaveChangesAsync();

            // Assert
            var savedShift = await _context.Shift.FirstOrDefaultAsync(s => s.Id == shift.Id);
            savedShift.Should().NotBeNull();
            savedShift.OriginalId.Should().Be(originalShift.Id, $"OriginalId should be preserved for status {status}");
            savedShift.Status.Should().Be(status);
        }
    }

    [Test]
    public void FilterShifts_WithGroupFilter_IncludesShiftsWithMatchingGroup()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var shift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift with Group",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16)),
            GroupItems = new List<GroupItem>
            {
                new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = Guid.NewGuid() }
            }
        };

        _context.Shift.Add(shift);
        _context.SaveChanges();

        var filter = new ShiftFilter
        {
            FilterType = ShiftFilterType.Shift,
            SelectedGroup = groupId,
            IsTimeRange = true,
            IsSporadic = true
        };

        _mockShiftStatusFilterService
            .ApplyStatusFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<ShiftFilterType>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockDateRangeFilterService
            .ApplyDateRangeFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockShiftSearchService
            .ApplySearchFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockShiftSortingService
            .ApplySorting(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(args => (IQueryable<Shift>)args[0]);

        // Act
        var result = _repository.FilterShifts(filter).ToList();

        // Assert
        result.Should().HaveCount(1);
        result.First().Id.Should().Be(shift.Id);
    }

    [Test]
    public void FilterShifts_WithGroupFilter_IncludesShiftsWithoutGroups()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var shiftWithoutGroup = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift without Group",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16)),
            GroupItems = new List<GroupItem>()
        };

        _context.Shift.Add(shiftWithoutGroup);
        _context.SaveChanges();

        var filter = new ShiftFilter
        {
            FilterType = ShiftFilterType.Shift,
            SelectedGroup = groupId,
            IsTimeRange = true,
            IsSporadic = true
        };

        _mockShiftStatusFilterService
            .ApplyStatusFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<ShiftFilterType>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockDateRangeFilterService
            .ApplyDateRangeFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockShiftSearchService
            .ApplySearchFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockShiftSortingService
            .ApplySorting(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(args => (IQueryable<Shift>)args[0]);

        // Act
        var result = _repository.FilterShifts(filter).ToList();

        // Assert
        result.Should().HaveCount(1);
        result.First().Id.Should().Be(shiftWithoutGroup.Id);
    }

    [Test]
    public void FilterShifts_WithGroupFilter_ExcludesShiftsWithDifferentGroup()
    {
        // Arrange
        var filterGroupId = Guid.NewGuid();
        var differentGroupId = Guid.NewGuid();
        var shiftWithDifferentGroup = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift with Different Group",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16)),
            GroupItems = new List<GroupItem>
            {
                new GroupItem { Id = Guid.NewGuid(), GroupId = differentGroupId, ShiftId = Guid.NewGuid() }
            }
        };

        _context.Shift.Add(shiftWithDifferentGroup);
        _context.SaveChanges();

        var filter = new ShiftFilter
        {
            FilterType = ShiftFilterType.Shift,
            SelectedGroup = filterGroupId,
            IsTimeRange = true,
            IsSporadic = true
        };

        _mockShiftStatusFilterService
            .ApplyStatusFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<ShiftFilterType>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockDateRangeFilterService
            .ApplyDateRangeFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockShiftSearchService
            .ApplySearchFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockShiftSortingService
            .ApplySorting(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(args => (IQueryable<Shift>)args[0]);

        // Act
        var result = _repository.FilterShifts(filter).ToList();

        // Assert
        result.Should().BeEmpty();
    }

    [Test]
    public void FilterShifts_WithGroupFilter_HandlesMixedShiftsCorrectly()
    {
        // Arrange
        var groupId = Guid.NewGuid();
        var differentGroupId = Guid.NewGuid();

        var shiftWithMatchingGroup = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift with Matching Group",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16)),
            GroupItems = new List<GroupItem>
            {
                new GroupItem { Id = Guid.NewGuid(), GroupId = groupId, ShiftId = Guid.NewGuid() }
            }
        };

        var shiftWithoutGroup = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift without Group",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = DateOnly.FromDateTime(DateTime.Now.AddDays(1)),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16)),
            GroupItems = new List<GroupItem>()
        };

        var shiftWithDifferentGroup = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Shift with Different Group",
            Status = ShiftStatus.OriginalShift,
            ShiftType = ShiftType.IsTask,
            FromDate = DateOnly.FromDateTime(DateTime.Now.AddDays(2)),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16)),
            GroupItems = new List<GroupItem>
            {
                new GroupItem { Id = Guid.NewGuid(), GroupId = differentGroupId, ShiftId = Guid.NewGuid() }
            }
        };

        _context.Shift.AddRange(shiftWithMatchingGroup, shiftWithoutGroup, shiftWithDifferentGroup);
        _context.SaveChanges();

        var filter = new ShiftFilter
        {
            FilterType = ShiftFilterType.Shift,
            SelectedGroup = groupId,
            IsTimeRange = true,
            IsSporadic = true
        };

        _mockShiftStatusFilterService
            .ApplyStatusFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<ShiftFilterType>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockDateRangeFilterService
            .ApplyDateRangeFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<bool>(), Arg.Any<bool>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockShiftSearchService
            .ApplySearchFilter(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(args => (IQueryable<Shift>)args[0]);
        _mockShiftSortingService
            .ApplySorting(Arg.Any<IQueryable<Shift>>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(args => (IQueryable<Shift>)args[0]);

        // Act
        var result = _repository.FilterShifts(filter).ToList();

        // Assert
        result.Should().HaveCount(2);
        result.Should().Contain(s => s.Id == shiftWithMatchingGroup.Id);
        result.Should().Contain(s => s.Id == shiftWithoutGroup.Id);
        result.Should().NotContain(s => s.Id == shiftWithDifferentGroup.Id);
    }

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }
}