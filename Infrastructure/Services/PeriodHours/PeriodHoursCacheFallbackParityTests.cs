// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Cross-path invariant for period hours: the two entry points that attribute a client's period
/// hours - <c>PeriodHoursService.GetPeriodHoursAsync</c> (the cache-populating core path, exercised here
/// through its private <c>CalculatePeriodHoursForClientsAsync</c> helper since no <c>ClientPeriodHours</c>
/// row exists) and <c>WorkRepository.GetPeriodHoursForClients</c> (the cache-miss fallback used when the
/// cache row is absent) - MUST weight work changes identically, otherwise a client's displayed hours
/// would silently depend on whether its period happened to be cached. Both entry points share the
/// same public shape (client IDs + date range in, <c>Dictionary&lt;Guid, PeriodHoursResource&gt;</c> out),
/// so their <c>Hours</c> values are compared directly against each other and against one hand-computed
/// expectation, which guards against a shared bug making both paths equally wrong.
/// </summary>

namespace Klacks.UnitTest.Infrastructure.Services.PeriodHours;

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Macros;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Schedules;
using Klacks.Api.Infrastructure.Services.PeriodHours;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using Klacks.Api.Infrastructure.Services.Clients;

[TestFixture]
public class PeriodHoursCacheFallbackParityTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ReplacementClientId = Guid.NewGuid();
    private static readonly DateOnly Start = new(2026, 3, 1);
    private static readonly DateOnly End = new(2026, 3, 31);
    private static readonly DateOnly WorkDay = new(2026, 3, 10);

    private DataBaseContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task PeriodHours_CachePathAndFallbackPath_ProduceIdenticalSurcharges()
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            CurrentDate = WorkDay,
            WorkTime = 5m,
            Surcharges = 1m,
            AnalyseToken = null,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(13, 0),
        };
        _context.Work.Add(work);

        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            CurrentDate = WorkDay.AddDays(1),
            WorkTime = 0m,
            Surcharges = 0.5m,
            AnalyseToken = null,
        });

        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Work = work,
            Type = WorkChangeType.CorrectionStart,
            ChangeTime = 2m,
            Surcharges = 0.25m,
            ToInvoice = true,
            AnalyseToken = null,
            StartTime = new TimeOnly(13, 0),
            EndTime = new TimeOnly(15, 0),
        });

        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Work = work,
            Type = WorkChangeType.ReplacementStart,
            ChangeTime = 1m,
            Surcharges = 0.1m,
            ToInvoice = true,
            ReplaceClientId = ReplacementClientId,
            AnalyseToken = null,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(9, 0),
        });
        await _context.SaveChangesAsync();

        var (periodHoursService, workRepository) = CreateServices();
        var clientIds = new List<Guid> { ClientId, ReplacementClientId };

        var core = await periodHoursService.GetPeriodHoursAsync(clientIds, Start, End);
        var fallback = await workRepository.GetPeriodHoursForClients(clientIds, Start, End);

        // Original client: work 1 + break 0.5 + correction 0.25 - handed-over replacement 0.1 = 1.65.
        // The billable flag is an invoice marker and never adds the correction's ChangeTime as a surcharge.
        const decimal expectedOriginal = 1.65m;
        const decimal expectedReplacement = 0.1m;

        core[ClientId].Surcharges.ShouldBe(expectedOriginal);
        core[ReplacementClientId].Surcharges.ShouldBe(expectedReplacement);
        fallback[ClientId].Surcharges.ShouldBe(expectedOriginal);
        fallback[ReplacementClientId].Surcharges.ShouldBe(expectedReplacement);
    }

    [Test]
    public async Task PeriodHours_AnUnpaidSubBreakSavedInTheContainerDialog_TakesItsHoursAndSurchargeOffOnce()
    {
        var unpaid = new Absence { Id = Guid.NewGuid(), IsUnpaid = true };
        var shift = new Shift { Id = Guid.NewGuid(), MacroId = Guid.NewGuid() };
        var container = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            ShiftId = shift.Id,
            CurrentDate = WorkDay,
            AnalyseToken = null,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(16, 0),
        };
        var subBreak = new Break
        {
            Id = Guid.NewGuid(),
            ParentWorkId = container.Id,
            ClientId = ClientId,
            AbsenceId = unpaid.Id,
            CurrentDate = WorkDay,
            AnalyseToken = null,
            StartTime = new TimeOnly(12, 0),
            EndTime = new TimeOnly(13, 0),
        };
        _context.Absence.Add(unpaid);
        _context.Shift.Add(shift);
        _context.Work.Add(container);
        _context.Break.Add(subBreak);
        await _context.SaveChangesAsync();

        const decimal containerSurcharges = 4m;
        const decimal containerHours = 8m;
        const decimal subBreakHours = 1m;
        const decimal subBreakSurcharges = 1m;
        var workMacros = Substitute.For<IWorkMacroService>();
        workMacros.ProcessWorkMacroAsync(Arg.Any<Work>()).Returns(call =>
        {
            call.Arg<Work>().Surcharges = containerSurcharges;
            call.Arg<Work>().WorkTime = containerHours;
            return true;
        });
        var macroData = Substitute.For<IMacroDataProvider>();
        macroData.GetMacroDataAsync(Arg.Any<Work>()).Returns(new MacroData());
        var macroCompilation = Substitute.For<IMacroCompilationService>();
        macroCompilation.CompileAndExecuteAsync(shift.MacroId.Value, Arg.Any<MacroData>())
            .Returns(new MacroExecutionResult(true, subBreakSurcharges, []));
        var manager = new ContainerWorkChildrenManager(
            _context, new EntityCollectionUpdateService(_context), workMacros, macroData, macroCompilation,
            Substitute.For<ILogger<ContainerWorkChildrenManager>>());

        await manager.UpdateChildrenAsync(container.Id, null, null, null, null, [], [subBreak], [], CancellationToken.None);
        await _context.SaveChangesAsync();

        var (periodHoursService, workRepository) = CreateServices();
        var clientIds = new List<Guid> { ClientId };
        var core = await periodHoursService.GetPeriodHoursAsync(clientIds, Start, End);
        var fallback = await workRepository.GetPeriodHoursForClients(clientIds, Start, End);

        const decimal expected = containerSurcharges - subBreakSurcharges;
        core[ClientId].Surcharges.ShouldBe(expected);
        fallback[ClientId].Surcharges.ShouldBe(expected);
        core[ClientId].Hours.ShouldBe(containerHours - subBreakHours);
        fallback[ClientId].Hours.ShouldBe(containerHours - subBreakHours);
    }

    [Test]
    public async Task PeriodHours_AContainerChildOfTheSamePerson_CountsNothingOfItsOwn_ACustomerChildStays()
    {
        var customerId = Guid.NewGuid();
        var container = NewContainerWork(ClientId, null, 8m, 2m);
        var ownChild = NewContainerWork(ClientId, container.Id, 3m, 1m);
        var customerChild = NewContainerWork(customerId, container.Id, 3m, 0m);
        _context.Work.AddRange(container, ownChild, customerChild);
        await _context.SaveChangesAsync();

        var (periodHoursService, workRepository) = CreateServices();
        var clientIds = new List<Guid> { ClientId, customerId };
        var core = await periodHoursService.GetPeriodHoursAsync(clientIds, Start, End);
        var fallback = await workRepository.GetPeriodHoursForClients(clientIds, Start, End);

        foreach (var result in new[] { core, fallback })
        {
            result[ClientId].Hours.ShouldBe(container.WorkTime);
            result[ClientId].Surcharges.ShouldBe(container.Surcharges);
            result[customerId].Hours.ShouldBe(customerChild.WorkTime);
        }
    }

    [Test]
    public async Task PeriodHours_TravelTimeOnAnInnerService_CountsForNeitherThePersonNorTheCustomer()
    {
        var customerId = Guid.NewGuid();
        var container = NewContainerWork(ClientId, null, 8m, 0m);
        var ownChild = NewContainerWork(ClientId, container.Id, 3m, 0m);
        var customerChild = NewContainerWork(customerId, container.Id, 3m, 0m);
        _context.Work.AddRange(container, ownChild, customerChild);
        foreach (var child in new[] { ownChild, customerChild })
        {
            _context.WorkChange.Add(new WorkChange
            {
                Id = Guid.NewGuid(),
                WorkId = child.Id,
                Work = child,
                Type = WorkChangeType.CorrectionStart,
                ChangeTime = 1m,
                Surcharges = 0.5m,
                AnalyseToken = null,
                StartTime = new TimeOnly(7, 0),
                EndTime = new TimeOnly(8, 0),
            });
        }

        await _context.SaveChangesAsync();

        var (periodHoursService, workRepository) = CreateServices();
        var clientIds = new List<Guid> { ClientId, customerId };
        var core = await periodHoursService.GetPeriodHoursAsync(clientIds, Start, End);
        var fallback = await workRepository.GetPeriodHoursForClients(clientIds, Start, End);

        foreach (var result in new[] { core, fallback })
        {
            result[ClientId].Hours.ShouldBe(container.WorkTime);
            result[ClientId].Surcharges.ShouldBe(0m);
            result[customerId].Hours.ShouldBe(customerChild.WorkTime);
            result[customerId].Surcharges.ShouldBe(0m);
        }
    }

    private static Work NewContainerWork(Guid clientId, Guid? parentWorkId, decimal workTime, decimal surcharges) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        ParentWorkId = parentWorkId,
        CurrentDate = WorkDay,
        WorkTime = workTime,
        Surcharges = surcharges,
        AnalyseToken = null,
        StartTime = new TimeOnly(8, 0),
        EndTime = new TimeOnly(16, 0),
    };

    private (PeriodHoursService PeriodHours, WorkRepository Works) CreateServices()
    {
        var contractDataProvider = Substitute.For<IClientContractDataProvider>();
        contractDataProvider
            .GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<int?>())
            .Returns(new Dictionary<Guid, EffectiveContractData>());

        var periodHoursService = new PeriodHoursService(
            _context,
            Substitute.For<ILogger<PeriodHoursService>>(),
            Substitute.For<IWorkNotificationService>(),
            Substitute.For<IClientGroupFilterService>(),
            contractDataProvider,
            Substitute.For<IWeekConfiguration>());

        var workRepository = new WorkRepository(
            _context,
            Substitute.For<ILogger<Work>>(),
            Substitute.For<IClientBaseQueryService>(),
            Substitute.For<IWorkMacroService>(),
            contractDataProvider);

        return (periodHoursService, workRepository);
    }

    [Test]
    public async Task PeriodHours_CachePathAndFallbackPath_ProduceIdenticalHours()
    {
        var work = new Work
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            CurrentDate = WorkDay,
            WorkTime = 5m,
            AnalyseToken = null,
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(13, 0),
        };
        _context.Work.Add(work);

        // Plain correction. Both paths add its raw ChangeTime unweighted.
        _context.WorkChange.Add(new WorkChange
        {
            Id = Guid.NewGuid(),
            WorkId = work.Id,
            Work = work,
            Type = WorkChangeType.CorrectionStart,
            ChangeTime = 2m,
            AnalyseToken = null,
            StartTime = new TimeOnly(13, 0),
            EndTime = new TimeOnly(15, 0),
        });
        await _context.SaveChangesAsync();

        var (periodHoursService, workRepository) = CreateServices();

        var clientIds = new List<Guid> { ClientId };

        // No ClientPeriodHours row exists for either path, so GetPeriodHoursAsync falls through to its
        // private CalculatePeriodHoursForClientsAsync (the cache-populating "core" calculation).
        var core = await periodHoursService.GetPeriodHoursAsync(clientIds, Start, End);

        // Same client/period, same absence of a cache row: GetPeriodHoursForClients falls through to its
        // BuildFallbackPeriodHours/CalculateWorkChangeAdjustments cache-miss branch.
        var fallback = await workRepository.GetPeriodHoursForClients(clientIds, Start, End);

        // 5 (work) + 2 (correction) = 7.
        const decimal expectedHours = 7m;

        core[ClientId].Hours.ShouldBe(expectedHours);
        fallback[ClientId].Hours.ShouldBe(expectedHours);
        core[ClientId].Hours.ShouldBe(fallback[ClientId].Hours);
    }
}
