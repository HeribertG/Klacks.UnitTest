// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterizes the roster TargetHoursDriftDetector scans over the real ClientRepository (EF InMemory) instead of a
/// stubbed IQueryable: employees and external employees that are not deleted are scanned, customers and deleted
/// clients are not; DetectAsync labels a client "FirstName Name" and GetActiveFingerprintsAsync scans the same ids.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Interfaces;
using Klacks.Api.Infrastructure.Services;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class TargetHoursDriftDetectorOverRepositoryTests
{
    private DataBaseContext _context = null!;
    private IWorkRepository _workRepository = null!;
    private TargetHoursDriftDetector _sut = null!;
    private List<Guid>? _scannedIds;

    [SetUp]
    public void SetUp()
    {
        _scannedIds = null;
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());

        var clientRepository = new ClientRepository(
            _context,
            Substitute.For<IMacroEngine>(),
            Substitute.For<IClientChangeTrackingService>(),
            Substitute.For<IClientEntityManagementService>(),
            new EntityCollectionUpdateService(_context),
            Substitute.For<IClientValidator>(),
            Substitute.For<ILogger<ClientRepository>>());

        _workRepository = Substitute.For<IWorkRepository>();
        _workRepository.GetPeriodHoursForClients(
                Arg.Do<List<Guid>>(ids => _scannedIds = ids),
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(call => ((List<Guid>)call[0]).ToDictionary(
                id => id, _ => new PeriodHoursResource { Hours = 100, GuaranteedHours = 160 }));

        var activityProbe = Substitute.For<IScheduleActivityProbe>();
        activityProbe.HasAnyWorkInRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(true);

        _sut = new TargetHoursDriftDetector(
            clientRepository, _workRepository, activityProbe, NullLogger<TargetHoursDriftDetector>.Instance,
            new FixedCompanyClock(new DateTimeOffset(2026, 8, 11, 12, 0, 0, TimeSpan.Zero)));
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task ScansEmployeesAndExternals_NotCustomersOrDeletedClients()
    {
        var employee = Seed("Anna", "Muster", EntityTypeEnum.Employee);
        var external = Seed("Beat", "Extern", EntityTypeEnum.ExternEmp);
        var unnamed = Seed(string.Empty, string.Empty, EntityTypeEnum.Employee);
        Seed("Carla", "Kundin", EntityTypeEnum.Customer);
        Seed("Dora", "Weg", EntityTypeEnum.Employee, isDeleted: true);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var events = await _sut.DetectAsync();

        _scannedIds.ShouldNotBeNull();
        _scannedIds.ShouldBe([employee.Id, external.Id, unnamed.Id], ignoreOrder: true);
        var affected = ((TargetHoursDriftTriggerEvent)events.Single()).AffectedClients;
        affected.Select(a => (a.ClientId, a.ClientName)).ShouldBe(
            [(employee.Id, "Anna Muster"), (external.Id, "Beat Extern"), (unnamed.Id, unnamed.Id.ToString())],
            ignoreOrder: true);
    }

    [Test]
    public async Task Fingerprints_ScanTheSameRoster()
    {
        var employee = Seed("Anna", "Muster", EntityTypeEnum.Employee);
        var external = Seed("Beat", "Extern", EntityTypeEnum.ExternEmp);
        Seed("Carla", "Kundin", EntityTypeEnum.Customer);
        Seed("Dora", "Weg", EntityTypeEnum.Employee, isDeleted: true);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        var fingerprints = await _sut.GetActiveFingerprintsAsync();

        fingerprints.Count.ShouldBe(1);
        _scannedIds.ShouldNotBeNull();
        _scannedIds.ShouldBe([employee.Id, external.Id], ignoreOrder: true);
    }

    [Test]
    public async Task OnlyCustomersAndDeletedClients_ScanNothing()
    {
        Seed("Carla", "Kundin", EntityTypeEnum.Customer);
        Seed("Dora", "Weg", EntityTypeEnum.Employee, isDeleted: true);
        await _context.SaveChangesAsync();
        _context.ChangeTracker.Clear();

        (await _sut.DetectAsync()).ShouldBeEmpty();
        (await _sut.GetActiveFingerprintsAsync()).ShouldBeEmpty();
        _scannedIds.ShouldBeNull();
    }

    private Client Seed(string firstName, string name, EntityTypeEnum type, bool isDeleted = false)
    {
        var client = new Client
        {
            Id = Guid.NewGuid(),
            FirstName = firstName,
            Name = name,
            Type = type,
            IsDeleted = isDeleted,
        };
        _context.Client.Add(client);
        return client;
    }
}
