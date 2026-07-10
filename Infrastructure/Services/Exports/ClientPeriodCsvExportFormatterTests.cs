// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClientPeriodCsvExportFormatter, verifying the RecordType discrimination,
/// culture-invariant decimal formatting, and — most importantly — that clients which only have
/// period hours (and no work entries) are NOT dropped from the CSV, matching the XML/JSON output.
/// </summary>
using System.Text;
using Shouldly;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class ClientPeriodCsvExportFormatterTests
{
    private ClientPeriodCsvExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new ClientPeriodCsvExportFormatter();
    }

    [Test]
    public void ContentType_And_FileExtension_AreCsv()
    {
        _formatter.FormatKey.ShouldBe(ExportConstants.FormatCsv);
        _formatter.ContentType.ShouldBe(ExportConstants.ContentTypeCsv);
        _formatter.FileExtension.ShouldBe(".csv");
    }

    [Test]
    public void Format_EmitsHeaderWithRecordTypeColumn()
    {
        var bytes = _formatter.Format(BuildData(), new ExportOptions());
        var lines = ReadLines(bytes);

        lines[0].ShouldStartWith("RecordType;ClientIdNumber;ClientName;ClientType;Date;EndDate;StartTime;EndTime;Hours;Surcharges;Information");
    }

    [Test]
    public void Format_WritesWorkRow_WithInvariantDecimalsAndEnumString()
    {
        var bytes = _formatter.Format(BuildData(), new ExportOptions());
        var lines = ReadLines(bytes);

        var workRow = lines.First(l => l.StartsWith("Work;"));
        var cells = workRow.Split(';');

        cells[3].ShouldBe("Employee");
        cells[4].ShouldBe("2026-01-10");
        cells[6].ShouldBe("08:00");
        cells[7].ShouldBe("16:30");
        cells[8].ShouldBe("8.50");
        cells[8].ShouldNotContain(",");
    }

    [Test]
    public void Format_KeepsClientWithOnlyPeriodHours_NotDropped()
    {
        var data = new ClientPeriodExportData
        {
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 31),
            Clients =
            [
                new ClientPeriodGroup
                {
                    ClientId = Guid.NewGuid(),
                    ClientName = "OnlyPeriod, Clara",
                    ClientIdNumber = 999,
                    ClientType = EntityTypeEnum.Employee,
                    WorkEntries = [],
                    PeriodHours =
                    [
                        new ClientPeriodHoursExportEntry
                        {
                            StartDate = new DateOnly(2026, 1, 1),
                            EndDate = new DateOnly(2026, 1, 31),
                            Hours = 160.25m,
                            Surcharges = 12.5m,
                            PaymentInterval = "Monthly",
                        },
                    ],
                },
            ],
        };

        var bytes = _formatter.Format(data, new ExportOptions());
        var lines = ReadLines(bytes);

        var periodRow = lines.FirstOrDefault(l => l.StartsWith("PeriodHours;"));
        periodRow.ShouldNotBeNull();

        var cells = periodRow!.Split(';');
        cells[1].ShouldBe("999");
        cells[4].ShouldBe("2026-01-01");
        cells[5].ShouldBe("2026-01-31");
        cells[8].ShouldBe("160.25");
        cells[9].ShouldBe("12.50");
        cells[10].ShouldBe("Monthly");
    }

    [Test]
    public void Format_EmitsClientRow_WhenClientHasNoWorkAndNoPeriodHours()
    {
        var data = new ClientPeriodExportData
        {
            Clients =
            [
                new ClientPeriodGroup
                {
                    ClientId = Guid.NewGuid(),
                    ClientName = "Empty, Dan",
                    ClientIdNumber = 500,
                    ClientType = EntityTypeEnum.ExternEmp,
                    WorkEntries = [],
                    PeriodHours = [],
                },
            ],
        };

        var bytes = _formatter.Format(data, new ExportOptions());
        var lines = ReadLines(bytes);

        var clientRow = lines.FirstOrDefault(l => l.StartsWith("Client;"));
        clientRow.ShouldNotBeNull();
        clientRow!.Split(';')[1].ShouldBe("500");
    }

    [Test]
    public void Format_EscapesSeparatorAndQuotesInText()
    {
        var data = new ClientPeriodExportData
        {
            Clients =
            [
                new ClientPeriodGroup
                {
                    ClientId = Guid.NewGuid(),
                    ClientName = "Semi;Colon \"Quote\"",
                    ClientIdNumber = 1,
                    ClientType = EntityTypeEnum.Employee,
                    WorkEntries =
                    [
                        new ClientWorkExportEntry
                        {
                            WorkId = Guid.NewGuid(),
                            WorkDate = new DateOnly(2026, 1, 5),
                            StartTime = new TimeOnly(8, 0),
                            EndTime = new TimeOnly(9, 0),
                            WorkTime = 1m,
                            Surcharges = 0m,
                            Information = "a;b",
                        },
                    ],
                },
            ],
        };

        var bytes = _formatter.Format(data, new ExportOptions());
        var text = Encoding.UTF8.GetString(bytes);

        text.ShouldContain("\"Semi;Colon \"\"Quote\"\"\"");
        text.ShouldContain("\"a;b\"");
    }

    [Test]
    public void Format_StartsWithUtf8Bom()
    {
        var bytes = _formatter.Format(BuildData(), new ExportOptions());
        var preamble = Encoding.UTF8.GetPreamble();

        bytes.Take(preamble.Length).ShouldBe(preamble);
    }

    private static string[] ReadLines(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes, Encoding.UTF8.GetPreamble().Length,
            bytes.Length - Encoding.UTF8.GetPreamble().Length);
        return text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
    }

    private static ClientPeriodExportData BuildData()
    {
        return new ClientPeriodExportData
        {
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 31),
            ExportDate = new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc),
            Clients =
            [
                new ClientPeriodGroup
                {
                    ClientId = Guid.NewGuid(),
                    ClientName = "Muster, Anna",
                    ClientIdNumber = 101,
                    ClientType = EntityTypeEnum.Employee,
                    WorkEntries =
                    [
                        new ClientWorkExportEntry
                        {
                            WorkId = Guid.NewGuid(),
                            WorkDate = new DateOnly(2026, 1, 10),
                            StartTime = new TimeOnly(8, 0),
                            EndTime = new TimeOnly(16, 30),
                            WorkTime = 8.5m,
                            Surcharges = 0m,
                            Information = "Note",
                        },
                    ],
                },
            ],
        };
    }
}
