// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for CsvExportFormatter: column positions per entry type, carriage return quoting,
/// and tolerance for unknown or non-Gregorian language codes.
/// </summary>
using System.Text;
using Shouldly;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class CsvExportFormatterColumnTests
{
    private const int DescriptionIndex = 20;
    private const int ExpenseTaxableIndex = 23;
    private const int InformationIndex = 25;
    private const int ToInvoiceIndex = 26;

    private CsvExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new CsvExportFormatter();
    }

    [Test]
    public void Header_KeepsExistingPositions_AndAppendsToInvoiceAtEnd()
    {
        var header = ReadLines(_formatter.Format(BuildData(), new ExportOptions()))[0].Split(';');

        header.Length.ShouldBe(27);
        header[DescriptionIndex].ShouldBe("Description");
        header[ExpenseTaxableIndex].ShouldBe("ExpenseTaxable");
        header[InformationIndex].ShouldBe("Information");
        header[ToInvoiceIndex].ShouldBe("ToInvoice");
    }

    [Test]
    public void AllRows_HaveSameColumnCountAsHeader()
    {
        var lines = ReadLines(_formatter.Format(BuildData(), new ExportOptions()));

        foreach (var line in lines)
        {
            line.Split(';').Length.ShouldBe(27, line);
        }
    }

    [Test]
    public void WorkRow_WritesInformationIntoInformationColumn_AndLeavesDescriptionEmpty()
    {
        var row = ReadLines(_formatter.Format(BuildData(), new ExportOptions())).First(l => l.Contains(";Work;")).Split(';');

        row[InformationIndex].ShouldBe("work info");
        row[DescriptionIndex].ShouldBe(string.Empty);
        row[ToInvoiceIndex].ShouldBe(string.Empty);
    }

    [Test]
    public void ChangeRow_WritesToInvoiceInOwnColumn_AndLeavesExpenseTaxableEmpty()
    {
        var row = ReadLines(_formatter.Format(BuildData(), new ExportOptions())).First(l => l.Contains(";WorkChange;")).Split(';');

        row[DescriptionIndex].ShouldBe("change desc");
        row[ExpenseTaxableIndex].ShouldBe(string.Empty);
        row[ToInvoiceIndex].ShouldBe("1");
    }

    [Test]
    public void ExpenseRow_WritesTaxableInExpenseTaxableColumn()
    {
        var row = ReadLines(_formatter.Format(BuildData(), new ExportOptions())).First(l => l.Contains(";Expenses;")).Split(';');

        row[22].ShouldBe("12.50");
        row[ExpenseTaxableIndex].ShouldBe("1");
        row[ToInvoiceIndex].ShouldBe(string.Empty);
    }

    [Test]
    public void Escape_QuotesLoneCarriageReturn()
    {
        var data = BuildData();
        data.Orders[0].WorkEntries[0].Information = "a\rb";

        var text = Encoding.UTF8.GetString(_formatter.Format(data, new ExportOptions()));

        text.ShouldContain("\"a\rb\"");
    }

    [TestCase("xx")]
    [TestCase("")]
    [TestCase("not a culture")]
    public void Format_DoesNotThrow_ForUnknownLanguage(string language)
    {
        Should.NotThrow(() => _formatter.Format(BuildData(), new ExportOptions { Language = language }));
    }

    [TestCase("th-TH")]
    [TestCase("ar-SA")]
    [TestCase("fa-IR")]
    public void Format_UsesGregorianYears_ForNonGregorianCultures(string language)
    {
        var lines = ReadLines(_formatter.Format(BuildData(), new ExportOptions { Language = language }));
        var row = lines.First(l => l.Contains(";Work;")).Split(';');

        row[14].ShouldBe("10.01.2026");
        lines.First(l => l.Contains(";Work;")).ShouldContain("01.01.2026");
    }

    private static string[] ReadLines(byte[] bytes)
    {
        var text = Encoding.UTF8.GetString(bytes).TrimStart('﻿');
        return text.Split(["\r\n", "\n"], StringSplitOptions.RemoveEmptyEntries);
    }

    private static OrderExportData BuildData()
    {
        return new OrderExportData
        {
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 31),
            Orders =
            [
                new OrderGroup
                {
                    OrderShiftId = Guid.NewGuid(),
                    OrderName = "Night Watch",
                    OrderAbbreviation = "NW",
                    OrderFromDate = new DateOnly(2026, 1, 1),
                    WorkEntries =
                    [
                        new WorkExportEntry
                        {
                            WorkId = Guid.NewGuid(),
                            EmployeeId = Guid.NewGuid(),
                            EmployeeName = "Worker, One",
                            EmployeeIdNumber = 1,
                            WorkDate = new DateOnly(2026, 1, 10),
                            StartTime = new TimeOnly(8, 0),
                            EndTime = new TimeOnly(16, 0),
                            WorkTime = 8m,
                            Information = "work info",
                            Changes =
                            [
                                new WorkChangeExportEntry
                                {
                                    Type = default,
                                    ChangeTime = 1m,
                                    StartTime = new TimeOnly(9, 0),
                                    EndTime = new TimeOnly(10, 0),
                                    Description = "change desc",
                                    ToInvoice = true,
                                },
                            ],
                            Expenses =
                            [
                                new ExpensesExportEntry { Amount = 12.5m, Description = "taxi", Taxable = true },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
