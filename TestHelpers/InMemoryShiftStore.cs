// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Services.ContainerTemplates;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// A fresh EF Core InMemory database with the real ShiftRepository and ContainerTemplateRepository on top, so a
/// caller's shift reads run through the repository's own query (filters, order, cap) instead of a substitute that
/// only replays a list. The store cannot prove SQL translation; the Npgsql integration tests do that.
/// </summary>
/// <param name="companyClock">Clock handed to the ShiftRepository; defaults to the current UTC instant</param>
public sealed class InMemoryShiftStore : IDisposable
{
    public InMemoryShiftStore(ICompanyClock? companyClock = null)
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString())
            .Options;

        Context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var collectionUpdateService = new EntityCollectionUpdateService(Context);

        Shifts = new ShiftRepository(
            Context,
            Substitute.For<ILogger<Shift>>(),
            Substitute.For<IShiftQueryPipelineService>(),
            Substitute.For<IShiftGroupManagementService>(),
            collectionUpdateService,
            Substitute.For<IShiftValidator>(),
            new ScheduleMapper(),
            companyClock ?? new FixedCompanyClock(DateTimeOffset.UtcNow));

        ContainerTemplates = new ContainerTemplateRepository(
            Context,
            Substitute.For<ILogger<ContainerTemplate>>(),
            collectionUpdateService,
            new ContainerTemplateService(Substitute.For<IUnitOfWork>(), Substitute.For<ILogger<ContainerTemplateService>>()));
    }

    public DataBaseContext Context { get; }

    public ShiftRepository Shifts { get; }

    public ContainerTemplateRepository ContainerTemplates { get; }

    public void Seed(params Shift[] shifts) => Seed((IEnumerable<Shift>)shifts);

    public void Seed(IEnumerable<Shift> shifts)
    {
        Context.Shift.AddRange(shifts);
        Context.SaveChanges();
        Context.ChangeTracker.Clear();
    }

    public void SeedTemplates(params ContainerTemplate[] templates)
    {
        Context.ContainerTemplate.AddRange(templates);
        Context.SaveChanges();
        Context.ChangeTracker.Clear();
    }

    public void Dispose() => Context.Dispose();
}
