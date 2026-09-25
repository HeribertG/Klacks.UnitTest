// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Characterization of the single-edit path for sealed works: WorkRepository.Put runs the macro pipeline for every work,
/// whatever its lock level (Confirmed, Approved, Closed); neither the repository nor the work PUT handler (which checks
/// only the sealed day) refuses a sealed work. So after a macro switch, editing a confirmed or approved work on an
/// unsealed day picks up the new macro; for a closed work the day lock of the PUT handler usually refuses the edit before
/// the repository is reached. Pinned so that a change of this behaviour is a conscious decision (owner decision F3 of
/// 2026-09-25: keep the behaviour, pin it).
/// </summary>

using Klacks.Api.Domain.Services.Common;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Repositories.Schedules;

[TestFixture]
public class WorkRepositorySealedPutTests
{
    [TestCase(WorkLockLevel.Confirmed)]
    [TestCase(WorkLockLevel.Approved)]
    [TestCase(WorkLockLevel.Closed)]
    public async Task Put_SealedWork_RunsTheMacroPipeline(WorkLockLevel lockLevel)
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        await using var context = new DataBaseContext(options, null!);
        var workMacroService = Substitute.For<IWorkMacroService>();
        var repository = new WorkRepository(
            context,
            NullLogger<Work>.Instance,
            Substitute.For<IClientBaseQueryService>(),
            workMacroService,
            Substitute.For<IClientContractDataProvider>());
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ShiftId = Guid.NewGuid(),
            ClientId = Guid.NewGuid(),
            CurrentDate = new DateOnly(2026, 6, 10),
            LockLevel = lockLevel
        };
        context.Work.Add(work);
        await context.SaveChangesAsync();
        context.ChangeTracker.Clear();

        await repository.Put(work);

        await workMacroService.Received(1).ProcessWorkMacroAsync(work);
    }
}
