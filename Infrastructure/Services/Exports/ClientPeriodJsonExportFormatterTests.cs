// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClientPeriodJsonExportFormatter, verifying real serialization: camelCase property
/// naming, native DateOnly/TimeOnly handling, and that period hours are included in the output.
/// </summary>
using System.Text;
using System.Text.Json;
using Shouldly;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class ClientPeriodJsonExportFormatterTests
{
    private ClientPeriodJsonExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new ClientPeriodJsonExportFormatter();
    }

    [Test]
    public void ContentType_And_FileExtension_AreJson()
    {
        _formatter.FormatKey.ShouldBe(ExportConstants.FormatJson);
        _formatter.ContentType.ShouldBe(ExportConstants.ContentTypeJson);
        _formatter.FileExtension.ShouldBe(".json");
    }

    [Test]
    public void Format_ProducesParseableCamelCaseJson_WithWorkAndPeriodHours()
    {
        var data = new ClientPeriodExportData
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
        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(bytes));

        var client = doc.RootElement.GetProperty("clients")[0];
        client.GetProperty("clientName").GetString().ShouldBe("Muster, Anna");
        client.GetProperty("clientIdNumber").GetInt32().ShouldBe(101);

        var work = client.GetProperty("workEntries")[0];
        work.GetProperty("workDate").GetString().ShouldBe("2026-01-10");
        work.GetProperty("startTime").GetString().ShouldBe("08:00:00");

        var period = client.GetProperty("periodHours")[0];
        period.GetProperty("hours").GetDecimal().ShouldBe(160.25m);
        period.GetProperty("paymentInterval").GetString().ShouldBe("Monthly");
    }
}
