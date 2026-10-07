// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Absences in the client period export: an absence on a day without work and a client with only absences are
/// exported, an absence is listed once even when the client has two works that day, on-call duty is flagged, and
/// the CSV/XML formatters write each absence exactly once.
/// </summary>
using System.Text;
using System.Xml.Linq;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Domain.Services.Common;
using Klacks.Api.Infrastructure.Services.Exports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class ClientPeriodExportAbsenceTests
{
    private static readonly DateOnly FromDate = new(2026, 1, 1);
    private static readonly DateOnly UntilDate = new(2026, 1, 31);
    private static readonly DateOnly WorkDay = new(2026, 1, 12);
    private static readonly DateOnly FreeDay = new(2026, 1, 18);

    private DataBaseContext _context = null!;
    private ClientPeriodExportDataLoader _loader = null!;
    private readonly Guid _vacationId = Guid.NewGuid();
    private readonly Guid _onCallId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _loader = new ClientPeriodExportDataLoader(_context);

        _context.Absence.Add(NewAbsence(_vacationId, "Ferien", isOnCall: false));
        _context.Absence.Add(NewAbsence(_onCallId, "Pikett", isOnCall: true));
        _context.SaveChanges();
    }

    [TearDown]
    public void TearDown() => _context.Dispose();

    [Test]
    public async Task ClientWithOnlyAnAbsence_IsExportedWithTheAbsence()
    {
        var clientId = AddClient(7);
        AddBreak(clientId, _onCallId, FreeDay, 0m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate);

        var client = data.Clients.ShouldHaveSingleItem();
        client.WorkEntries.ShouldBeEmpty();
        var absence = client.Absences.ShouldHaveSingleItem();
        absence.BreakDate.ShouldBe(FreeDay);
        absence.IsOnCall.ShouldBeTrue();
        absence.BreakTime.ShouldBe(0m);
    }

    [Test]
    public async Task AbsenceOnWorkDay_IsListedOnce_UnderTheEarliestWork()
    {
        var clientId = AddClient(8);
        var earlyWork = AddWork(clientId, WorkDay, new TimeOnly(6, 0));
        var lateWork = AddWork(clientId, WorkDay, new TimeOnly(14, 0));
        AddBreak(clientId, _vacationId, WorkDay, 2m);
        AddBreak(clientId, _vacationId, FreeDay, 8m);
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate);

        var client = data.Clients.ShouldHaveSingleItem();
        client.WorkEntries.Single(w => w.WorkId == earlyWork).Breaks.ShouldHaveSingleItem().BreakTime.ShouldBe(2m);
        client.WorkEntries.Single(w => w.WorkId == lateWork).Breaks.ShouldBeEmpty();
        client.Absences.ShouldHaveSingleItem().BreakDate.ShouldBe(FreeDay);
    }

    [Test]
    public async Task UnsealedAndScenarioAbsences_AreNotExported()
    {
        var clientId = AddClient(9);
        AddBreak(clientId, _vacationId, FreeDay, 8m, WorkLockLevel.None);
        AddBreak(clientId, _vacationId, FreeDay, 8m, analyseToken: Guid.NewGuid());
        await _context.SaveChangesAsync();

        var data = await _loader.LoadAsync(FromDate, UntilDate);

        data.Clients.ShouldBeEmpty();
    }

    [Test]
    public void MapBreaks_WithCarrierSet_OnlyTheCarrierListsTheDaysBreaks()
    {
        var clientId = Guid.NewGuid();
        var carrier = new Work { Id = Guid.NewGuid(), ClientId = clientId, CurrentDate = WorkDay, StartTime = new TimeOnly(6, 0) };
        var other = new Work { Id = Guid.NewGuid(), ClientId = clientId, CurrentDate = WorkDay, StartTime = new TimeOnly(14, 0) };
        var lookups = new WorkSubEntryLookups(
            [],
            [],
            new Dictionary<(Guid ClientId, DateOnly Date), List<Break>>
            {
                [(clientId, WorkDay)] = [new Break { ClientId = clientId, CurrentDate = WorkDay, WorkTime = 1m }],
            },
            WorkSubEntryMapper.SelectBreakCarrierWorkIds([other, carrier]));

        WorkSubEntryMapper.MapBreaks(carrier.Id, clientId, WorkDay, lookups).Count.ShouldBe(1);
        WorkSubEntryMapper.MapBreaks(other.Id, clientId, WorkDay, lookups).ShouldBeEmpty();
    }

    [Test]
    public void CsvFormatter_WritesEveryAbsenceOnce_WithOnCallMarker()
    {
        var data = SampleWithNestedAndFreeDayAbsence();

        var csv = Encoding.UTF8.GetString(new ClientPeriodCsvExportFormatter().Format(data, new ExportOptions()));
        var absenceRows = csv.Split(ExportConstants.LineEnding).Where(l => l.StartsWith("Absence;")).ToList();

        absenceRows.Count.ShouldBe(2);
        absenceRows[0].ShouldBe("Absence;5;Muster, Max;Employee;2026-01-12;;12:00;14:00;2.00;;Ferien;");
        absenceRows[1].ShouldBe("Absence;5;Muster, Max;Employee;2026-01-18;;00:00;23:59;0.00;;Pikett;1");
    }

    [Test]
    public void XmlFormatter_WritesFreeDayAbsencesUnderAbsences_WithOnCallElement()
    {
        var data = SampleWithNestedAndFreeDayAbsence();

        var xml = XDocument.Parse(Encoding.UTF8.GetString(new ClientPeriodXmlExportFormatter().Format(data, new ExportOptions())));
        var client = xml.Root!.Element("Client")!;

        client.Element("WorkEntries")!.Element("Work")!.Element("Breaks")!.Elements("Break").Count().ShouldBe(1);
        var freeDay = client.Element("Absences")!.Elements("Break").ShouldHaveSingleItem();
        freeDay.Element("Date")!.Value.ShouldBe("2026-01-18");
        freeDay.Element("OnCall")!.Value.ShouldBe("true");
    }

    private static ClientPeriodExportData SampleWithNestedAndFreeDayAbsence()
    {
        return new ClientPeriodExportData
        {
            StartDate = FromDate,
            EndDate = UntilDate,
            Clients =
            [
                new ClientPeriodGroup
                {
                    ClientId = Guid.NewGuid(),
                    ClientName = "Muster, Max",
                    ClientIdNumber = 5,
                    ClientType = EntityTypeEnum.Employee,
                    WorkEntries =
                    [
                        new ClientWorkExportEntry
                        {
                            WorkId = Guid.NewGuid(),
                            WorkDate = WorkDay,
                            StartTime = new TimeOnly(6, 0),
                            EndTime = new TimeOnly(12, 0),
                            WorkTime = 6m,
                            Breaks =
                            [
                                new BreakExportEntry
                                {
                                    AbsenceName = "Ferien",
                                    BreakDate = WorkDay,
                                    StartTime = new TimeOnly(12, 0),
                                    EndTime = new TimeOnly(14, 0),
                                    BreakTime = 2m,
                                },
                            ],
                        },
                    ],
                    Absences =
                    [
                        new BreakExportEntry
                        {
                            AbsenceName = "Pikett",
                            BreakDate = FreeDay,
                            StartTime = new TimeOnly(0, 0),
                            EndTime = new TimeOnly(23, 59),
                            BreakTime = 0m,
                            IsOnCall = true,
                        },
                    ],
                },
            ],
        };
    }

    private static Absence NewAbsence(Guid id, string name, bool isOnCall)
    {
        return new Absence
        {
            Id = id,
            Name = new MultiLanguage { De = name },
            Abbreviation = new MultiLanguage { De = name },
            Description = new MultiLanguage { De = name },
            IsOnCall = isOnCall,
        };
    }

    private Guid AddClient(int idNumber)
    {
        var clientId = Guid.NewGuid();
        _context.Client.Add(new Client
        {
            Id = clientId,
            Type = EntityTypeEnum.Employee,
            IdNumber = idNumber,
            Name = $"Employee{idNumber}",
            FirstName = "Test",
        });
        return clientId;
    }

    private Guid AddWork(Guid clientId, DateOnly date, TimeOnly start)
    {
        var id = Guid.NewGuid();
        _context.Work.Add(new Work
        {
            Id = id,
            ClientId = clientId,
            ShiftId = Guid.NewGuid(),
            CurrentDate = date,
            StartTime = start,
            EndTime = start.AddHours(4),
            WorkTime = 4m,
            LockLevel = WorkLockLevel.Closed,
        });
        return id;
    }

    private void AddBreak(
        Guid clientId,
        Guid absenceId,
        DateOnly date,
        decimal workTime,
        WorkLockLevel lockLevel = WorkLockLevel.Closed,
        Guid? analyseToken = null)
    {
        _context.Break.Add(new Break
        {
            Id = Guid.NewGuid(),
            ClientId = clientId,
            AbsenceId = absenceId,
            CurrentDate = date,
            StartTime = new TimeOnly(0, 0),
            EndTime = new TimeOnly(23, 59),
            WorkTime = workTime,
            LockLevel = lockLevel,
            AnalyseToken = analyseToken,
        });
    }
}
