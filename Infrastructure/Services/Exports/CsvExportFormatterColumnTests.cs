// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for CsvExportFormatter: column positions per entry type (no expense columns, because
/// expenses belong to the employee and never go into a customer order export), carriage return quoting,
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
    private const int ColumnCount = 25;
    private const int DescriptionIndex = 20;
    private const int ReplaceEmployeeIndex = 21;
    private const int AbsenceNameIndex = 22;
    private const int InformationIndex = 23;
    private const int ToInvoiceIndex = 24;

    private CsvExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new CsvExportFormatter();
    }

    [Test]
    public void Header_HasNoExpenseColumns_AndAppendsToInvoiceAtEnd()
    {
        var header = ReadLines(_formatter.Format(BuildData(), new ExportOptions()))[0].Split(';');

        header.Length.ShouldBe(ColumnCount);
        header.ShouldNotContain("ExpenseAmount");
        header.ShouldNotContain("ExpenseTaxable");
        header[DescriptionIndex].ShouldBe("Description");
        header[ReplaceEmployeeIndex].ShouldBe("ReplaceEmployee");
        header[AbsenceNameIndex].ShouldBe("AbsenceName");
        header[InformationIndex].ShouldBe("Information");
        header[ToInvoiceIndex].ShouldBe("ToInvoice");
    }

    [Test]
    public void AllRows_HaveSameColumnCountAsHeader()
    {
        var lines = ReadLines(_formatter.Format(BuildData(), new ExportOptions()));

        foreach (var line in lines)
        {
            line.Split(';').Length.ShouldBe(ColumnCount, line);
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
    public void ChangeRow_WritesToInvoiceInOwnColumn_AndLeavesAbsenceNameEmpty()
    {
        var row = ReadLines(_formatter.Format(BuildData(), new ExportOptions())).First(l => l.Contains(";WorkChange;")).Split(';');

        row[DescriptionIndex].ShouldBe("change desc");
        row[AbsenceNameIndex].ShouldBe(string.Empty);
        row[ToInvoiceIndex].ShouldBe("1");
    }

    [Test]
    public void BreakRow_WritesAbsenceNameIntoAbsenceNameColumn()
    {
        var row = ReadLines(_formatter.Format(BuildData(), new ExportOptions())).First(l => l.Contains(";Break;")).Split(';');

        row[AbsenceNameIndex].ShouldBe("Pause");
        row[InformationIndex].ShouldBe(string.Empty);
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
                            Breaks =
                            [
                                new BreakExportEntry
                                {
                                    AbsenceName = "Pause",
                                    BreakDate = new DateOnly(2026, 1, 10),
                                    StartTime = new TimeOnly(12, 0),
                                    EndTime = new TimeOnly(12, 30),
                                    BreakTime = 0.5m,
                                },
                            ],
                        },
                    ],
                },
            ],
        };
    }
}
