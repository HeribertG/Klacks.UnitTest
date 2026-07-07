// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClientPeriodXmlExportFormatter, verifying the XML structure, exact
/// EntityTypeEnum string rendering, and culture-invariant decimal formatting.
/// </summary>
using System.Text;
using System.Xml.Linq;
using Shouldly;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class ClientPeriodXmlExportFormatterTests
{
    private ClientPeriodXmlExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new ClientPeriodXmlExportFormatter();
    }

    [Test]
    public void ContentType_And_FileExtension_AreXml()
    {
        _formatter.ContentType.ShouldBe(ExportConstants.ContentTypeXml);
        _formatter.FileExtension.ShouldBe(".xml");
    }

    [Test]
    public void Format_ProducesRootElementWithExpectedNameAndAttributes()
    {
        var data = BuildData();
        var options = new ExportOptions { CurrencyCode = "EUR" };

        var bytes = _formatter.Format(data, options);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(bytes));

        xml.Root!.Name.LocalName.ShouldBe("ClientPeriodExport");
        xml.Root.Attribute("startDate")!.Value.ShouldBe("2026-01-01");
        xml.Root.Attribute("endDate")!.Value.ShouldBe("2026-01-31");
        xml.Root.Attribute("currency")!.Value.ShouldBe("EUR");
        xml.Root.Attribute("exportDate").ShouldNotBeNull();
    }

    [Test]
    public void Format_WritesClientTypeAsExactEnumString_ForEmployeeAndExternEmp()
    {
        var data = BuildData();
        var options = new ExportOptions { CurrencyCode = "EUR" };

        var bytes = _formatter.Format(data, options);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(bytes));

        var clientElements = xml.Root!.Elements("Client").ToList();
        clientElements.Count.ShouldBe(2);

        clientElements[0].Element("Type")!.Value.ShouldBe("Employee");
        clientElements[1].Element("Type")!.Value.ShouldBe("ExternEmp");
    }

    [Test]
    public void Format_WritesHoursAsInvariantCultureF2String()
    {
        var data = BuildData();
        var options = new ExportOptions { CurrencyCode = "EUR" };

        var bytes = _formatter.Format(data, options);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(bytes));

        var hours = xml.Root!.Elements("Client").First()
            .Element("WorkEntries")!.Elements("Work").First()
            .Element("Hours")!.Value;

        hours.ShouldBe("8.50");
        hours.ShouldNotContain(",");
    }

    [Test]
    public void Format_WritesChangesExpensesAndBreaks_WhenPresent()
    {
        var data = BuildData();
        var options = new ExportOptions { CurrencyCode = "EUR" };

        var bytes = _formatter.Format(data, options);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(bytes));

        var work = xml.Root!.Elements("Client").First()
            .Element("WorkEntries")!.Elements("Work").First();

        work.Element("Changes")!.Elements("Change").Count().ShouldBe(1);
        work.Element("Expenses")!.Elements("Expense").Count().ShouldBe(1);
        work.Element("Breaks")!.Elements("Break").Count().ShouldBe(1);
    }

    [Test]
    public void Format_OmitsInformationElement_WhenNull()
    {
        var data = BuildData();
        var options = new ExportOptions { CurrencyCode = "EUR" };

        var bytes = _formatter.Format(data, options);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(bytes));

        var externWork = xml.Root!.Elements("Client").Last()
            .Element("WorkEntries")!.Elements("Work").First();

        externWork.Element("Information").ShouldBeNull();
    }

    [Test]
    public void Format_WritesPeriodHoursBlock_WithAttributesAndInvariantDecimals()
    {
        var data = BuildData();
        data.Clients[0].PeriodHours =
        [
            new ClientPeriodHoursExportEntry
            {
                StartDate = new DateOnly(2026, 1, 1),
                EndDate = new DateOnly(2026, 1, 31),
                Hours = 160.25m,
                Surcharges = 12.5m,
                PaymentInterval = "Monthly",
            },
            new ClientPeriodHoursExportEntry
            {
                StartDate = new DateOnly(2026, 2, 1),
                EndDate = new DateOnly(2026, 2, 28),
                Hours = 152m,
                Surcharges = 0m,
                PaymentInterval = "Weekly",
            },
        ];
        var options = new ExportOptions { CurrencyCode = "EUR" };

        var bytes = _formatter.Format(data, options);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(bytes));

        var periods = xml.Root!.Elements("Client").First()
            .Element("PeriodHours")!.Elements("Period").ToList();

        periods.Count.ShouldBe(2);
        periods[0].Attribute("startDate")!.Value.ShouldBe("2026-01-01");
        periods[0].Attribute("endDate")!.Value.ShouldBe("2026-01-31");
        periods[0].Element("Hours")!.Value.ShouldBe("160.25");
        periods[0].Element("Surcharges")!.Value.ShouldBe("12.50");
        periods[0].Element("PaymentInterval")!.Value.ShouldBe("Monthly");
        periods[1].Element("PaymentInterval")!.Value.ShouldBe("Weekly");
    }

    [Test]
    public void Format_OmitsPeriodHoursBlock_WhenEmpty()
    {
        var data = BuildData();
        var options = new ExportOptions { CurrencyCode = "EUR" };

        var bytes = _formatter.Format(data, options);
        var xml = XDocument.Parse(Encoding.UTF8.GetString(bytes));

        xml.Root!.Elements("Client").First().Element("PeriodHours").ShouldBeNull();
    }

    private static ClientPeriodExportData BuildData()
    {
        var employeeWorkId = Guid.NewGuid();

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
                            WorkId = employeeWorkId,
                            WorkDate = new DateOnly(2026, 1, 10),
                            StartTime = new TimeOnly(8, 0),
                            EndTime = new TimeOnly(16, 30),
                            WorkTime = 8.5m,
                            Surcharges = 0m,
                            Information = "Note",
                            Changes =
                            [
                                new WorkChangeExportEntry
                                {
                                    Type = WorkChangeType.CorrectionStart,
                                    ChangeTime = 0.5m,
                                    StartTime = new TimeOnly(8, 0),
                                    EndTime = new TimeOnly(8, 30),
                                    Description = "Late start",
                                    Surcharges = 0m,
                                    ToInvoice = true,
                                },
                            ],
                            Expenses =
                            [
                                new ExpensesExportEntry
                                {
                                    Amount = 12.5m,
                                    Description = "Parking",
                                    Taxable = true,
                                },
                            ],
                            Breaks =
                            [
                                new BreakExportEntry
                                {
                                    AbsenceName = "Lunch",
                                    BreakDate = new DateOnly(2026, 1, 10),
                                    StartTime = new TimeOnly(12, 0),
                                    EndTime = new TimeOnly(13, 0),
                                    BreakTime = 1m,
                                },
                            ],
                        },
                    ],
                },
                new ClientPeriodGroup
                {
                    ClientId = Guid.NewGuid(),
                    ClientName = "Extern, Bob",
                    ClientIdNumber = 201,
                    ClientType = EntityTypeEnum.ExternEmp,
                    WorkEntries =
                    [
                        new ClientWorkExportEntry
                        {
                            WorkId = Guid.NewGuid(),
                            WorkDate = new DateOnly(2026, 1, 15),
                            StartTime = new TimeOnly(9, 0),
                            EndTime = new TimeOnly(17, 0),
                            WorkTime = 8m,
                            Surcharges = 0m,
                            Information = null,
                        },
                    ],
                },
            ],
        };
    }
}
