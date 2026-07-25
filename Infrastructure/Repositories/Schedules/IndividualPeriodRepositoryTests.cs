// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for IndividualPeriodRepository against a real DataBaseContext (InMemory provider) and a
/// real EntityCollectionUpdateService: FK assignment on Add, in-place Put without losing the
/// parent's CreateTime, Period collection sync (add/update/soft-delete), and cascading soft
/// delete of Period children.
/// </summary>

using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Repositories.Schedules;

[TestFixture]
public class IndividualPeriodRepositoryTests
{
    private DataBaseContext _context = null!;
    private IndividualPeriodRepository _repository = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;

        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        var collectionUpdateService = new EntityCollectionUpdateService(_context);
        _repository = new IndividualPeriodRepository(_context, Substitute.For<ILogger<IndividualPeriod>>(), collectionUpdateService);
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task Add_SetsForeignKeyOnPeriods()
    {
        var periodId = Guid.NewGuid();
        var individualPeriod = new IndividualPeriod
        {
            Id = Guid.NewGuid(),
            Name = "Custom cycle",
            Periods = [new Period { Id = periodId, FromDate = new DateOnly(2026, 1, 1), FullHours = 160m }],
        };

        await _repository.Add(individualPeriod);
        await _context.SaveChangesAsync();

        var savedPeriod = await _context.Period.FirstAsync(p => p.Id == periodId);
        savedPeriod.IndividualPeriodId.ShouldBe(individualPeriod.Id);
    }

    [Test]
    public async Task Put_UpdatesExistingIndividualPeriod_WithoutLosingCreateTime()
    {
        var individualPeriodId = Guid.NewGuid();

        _context.IndividualPeriod.Add(new IndividualPeriod { Id = individualPeriodId, Name = "Original", Periods = [] });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var originalCreateTime = (await _context.IndividualPeriod.FirstAsync(ip => ip.Id == individualPeriodId)).CreateTime;
        _context.ChangeTracker.Clear();

        var updateModel = new IndividualPeriod { Id = individualPeriodId, Name = "Updated", Periods = [] };

        var result = await _repository.Put(updateModel);

        result.ShouldNotBeNull();
        result!.CreateTime.ShouldBe(originalCreateTime);

        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var persisted = await _context.IndividualPeriod.FirstAsync(ip => ip.Id == individualPeriodId);
        persisted.Name.ShouldBe("Updated");
        persisted.CreateTime.ShouldBe(originalCreateTime);
    }

    [Test]
    public async Task Put_AddsNewPeriod()
    {
        var individualPeriodId = Guid.NewGuid();

        _context.IndividualPeriod.Add(new IndividualPeriod { Id = individualPeriodId, Name = "Cycle", Periods = [] });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var updateModel = new IndividualPeriod
        {
            Id = individualPeriodId,
            Name = "Cycle",
            Periods = [new Period { FromDate = new DateOnly(2026, 1, 1), FullHours = 160m }],
        };

        await _repository.Put(updateModel);
        await _context.SaveChangesAsync();

        var periods = await _context.Period.Where(p => p.IndividualPeriodId == individualPeriodId).ToListAsync();
        periods.Count.ShouldBe(1);
        periods[0].FullHours.ShouldBe(160m);
    }

    [Test]
    public async Task Put_UpdatesExistingPeriod_InPlace()
    {
        var individualPeriodId = Guid.NewGuid();
        var periodId = Guid.NewGuid();

        _context.IndividualPeriod.Add(new IndividualPeriod
        {
            Id = individualPeriodId,
            Name = "Cycle",
            Periods = [new Period { Id = periodId, IndividualPeriodId = individualPeriodId, FromDate = new DateOnly(2026, 1, 1), FullHours = 160m }],
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var updateModel = new IndividualPeriod
        {
            Id = individualPeriodId,
            Name = "Cycle",
            Periods = [new Period { Id = periodId, FromDate = new DateOnly(2026, 1, 1), FullHours = 180m }],
        };

        await _repository.Put(updateModel);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var periods = await _context.Period.Where(p => p.IndividualPeriodId == individualPeriodId).ToListAsync();
        periods.Count.ShouldBe(1);
        periods[0].Id.ShouldBe(periodId);
        periods[0].FullHours.ShouldBe(180m);
    }

    [Test]
    public async Task Put_RemovesMissingPeriod_ViaSoftDelete()
    {
        var individualPeriodId = Guid.NewGuid();
        var periodId = Guid.NewGuid();

        _context.IndividualPeriod.Add(new IndividualPeriod
        {
            Id = individualPeriodId,
            Name = "Cycle",
            Periods = [new Period { Id = periodId, IndividualPeriodId = individualPeriodId, FromDate = new DateOnly(2026, 1, 1), FullHours = 160m }],
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var updateModel = new IndividualPeriod { Id = individualPeriodId, Name = "Cycle", Periods = [] };

        await _repository.Put(updateModel);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var deletedPeriod = await _context.Period.IgnoreQueryFilters().FirstAsync(p => p.Id == periodId);
        deletedPeriod.IsDeleted.ShouldBeTrue();

        var remainingActivePeriods = await _context.Period.Where(p => p.IndividualPeriodId == individualPeriodId).ToListAsync();
        remainingActivePeriods.ShouldBeEmpty();
    }

    [Test]
    public async Task Delete_CascadesSoftDeleteToActivePeriodChildren()
    {
        var individualPeriodId = Guid.NewGuid();
        var periodId = Guid.NewGuid();

        _context.IndividualPeriod.Add(new IndividualPeriod
        {
            Id = individualPeriodId,
            Name = "Cycle",
            Periods = [new Period { Id = periodId, IndividualPeriodId = individualPeriodId, FromDate = new DateOnly(2026, 1, 1), FullHours = 160m }],
        });
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var deleted = await _repository.Delete(individualPeriodId);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        deleted.ShouldNotBeNull();

        var deletedIndividualPeriod = await _context.IndividualPeriod.IgnoreQueryFilters().FirstAsync(ip => ip.Id == individualPeriodId);
        deletedIndividualPeriod.IsDeleted.ShouldBeTrue();

        var deletedPeriod = await _context.Period.IgnoreQueryFilters().FirstAsync(p => p.Id == periodId);
        deletedPeriod.IsDeleted.ShouldBeTrue();
    }

    [Test]
    public async Task Delete_WithNonExistentId_ReturnsNull()
    {
        var result = await _repository.Delete(Guid.NewGuid());

        result.ShouldBeNull();
    }
}
