// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins that WorkChangeRepository.Add tracks the new entity before the work change macro runs, so the
/// macro sees the EF-assigned Id and the entity as Added (the effective-time window of a new correction
/// is computed from its position among its tracked siblings).
/// </summary>

using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Repositories.Schedules;

[TestFixture]
public class WorkChangeRepositoryMacroOrderTests
{
    private DataBaseContext _context = null!;
    private IWorkMacroService _macroService = null!;
    private WorkChangeRepository _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, null!);
        _macroService = Substitute.For<IWorkMacroService>();
        _sut = new WorkChangeRepository(_context, NullLogger<WorkChange>.Instance, _macroService);
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task Add_RunsMacroAfterEntityIsTracked_WithAssignedId()
    {
        var workChange = new WorkChange
        {
            WorkId = Guid.NewGuid(),
            Type = WorkChangeType.CorrectionEnd,
            ChangeTime = 0.5m,
        };
        var idSeenByMacro = Guid.Empty;
        var stateSeenByMacro = EntityState.Detached;
        _macroService
            .When(service => service.ProcessWorkChangeMacroAsync(workChange))
            .Do(_ =>
            {
                idSeenByMacro = workChange.Id;
                stateSeenByMacro = _context.Entry(workChange).State;
            });

        await _sut.Add(workChange);

        await _macroService.Received(1).ProcessWorkChangeMacroAsync(workChange);
        idSeenByMacro.ShouldNotBe(Guid.Empty);
        stateSeenByMacro.ShouldBe(EntityState.Added);
    }

    [Test]
    public async Task Add_SurchargeItemsAddedByMacroAfterTracking_ArePersisted()
    {
        var workChange = new WorkChange
        {
            WorkId = Guid.NewGuid(),
            Type = WorkChangeType.CorrectionEnd,
            ChangeTime = 0.5m,
        };
        _macroService
            .When(service => service.ProcessWorkChangeMacroAsync(workChange))
            .Do(_ => workChange.SurchargeItems.Add(new SurchargeItem { Type = SurchargeType.Night, Amount = 0.05m }));

        await _sut.Add(workChange);
        await _context.SaveChangesAsync();

        var persisted = await _context.SurchargeItem.AsNoTracking().ToListAsync();
        persisted.Count.ShouldBe(1);
        persisted[0].WorkChangeId.ShouldBe(workChange.Id);
    }
}
