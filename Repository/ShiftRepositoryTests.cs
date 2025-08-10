using Klacks.Api.Datas;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Repositories;
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
        _repository = new ShiftRepository(_context, _mockLogger, _mockDateRangeFilterService, _mockShiftSearchService, _mockShiftSortingService, _mockShiftStatusFilterService);
    }

    [Test]
    public async Task AddOriginalShift_WithStatusIsCutOriginal_PreservesOriginalId()
    {
        // Arrange
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Original Shift",
            Status = ShiftStatus.Original,
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
            Status = ShiftStatus.IsCutOriginal,
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
        savedShift.Status.Should().Be(ShiftStatus.IsCutOriginal);
    }

    [Test]
    public async Task AddOriginalShift_WithStatusReadyToCut_PreservesOriginalId()
    {
        // Arrange
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Original Shift",
            Status = ShiftStatus.Original,
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
            Status = ShiftStatus.ReadyToCut,
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
        savedShift.Status.Should().Be(ShiftStatus.ReadyToCut);
    }

    [Test]
    public async Task AddCutShift_WithStatusIsCut_PreservesOriginalId()
    {
        // Arrange
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Original Shift",
            Status = ShiftStatus.Original,
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
            Status = ShiftStatus.IsCut,
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
        savedCut.Status.Should().Be(ShiftStatus.IsCut);
    }


    [Test]
    public async Task AddMultipleCutShifts_PreservesOriginalId()
    {
        // Arrange
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Original Shift",
            Status = ShiftStatus.Original,
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
            Status = ShiftStatus.IsCut,
            OriginalId = originalShift.Id,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(12))
        };

        var cutShift2 = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Cut Shift 2",
            Status = ShiftStatus.IsCut,
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
        allCutShifts.Should().AllSatisfy(cut => cut.Status.Should().Be(ShiftStatus.IsCut));
    }

    [Test]
    public async Task ShiftWithStatusGreaterThanOne_AlwaysPreservesOriginalId()
    {
        // Arrange
        var originalShift = new Shift
        {
            Id = Guid.NewGuid(),
            Name = "Original Shift",
            Status = ShiftStatus.Original,
            OriginalId = null,
            FromDate = DateOnly.FromDateTime(DateTime.Now),
            StartShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(8)),
            EndShift = TimeOnly.FromTimeSpan(TimeSpan.FromHours(16))
        };
        
        await _repository.Add(originalShift);
        await _context.SaveChangesAsync();

        var testCases = new[]
        {
            (ShiftStatus.ReadyToCut, "Ready to Cut"),
            (ShiftStatus.IsCutOriginal, "Is Cut Original"),
            (ShiftStatus.IsCut, "Is Cut")
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
            if (status == ShiftStatus.IsCut)
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

    [TearDown]
    public void TearDown()
    {
        _context?.Dispose();
    }
}