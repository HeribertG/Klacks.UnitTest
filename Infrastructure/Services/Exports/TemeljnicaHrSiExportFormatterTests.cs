// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for TemeljnicaHrSiExportFormatter verifying the 22-column PANTHEON temeljnica
/// header row, work-entry booking values (account, amount, date) and the additional booking
/// line emitted per expense with taxable/non-taxable counter-account selection.
/// </summary>
using ClosedXML.Excel;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Services.Exports;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class TemeljnicaHrSiExportFormatterTests
{
    private static readonly string[] ExpectedHeaders =
    {
        "Številka temeljnice",
        "Datum obdobja za temeljnico",
        "Konto",
        "Subjekt",
        "Debet",
        "Kredit",
        "Dokument",
        "Vezni dokument",
        "Datum dokumenta",
        "Datum zapadlosti",
        "Valuta",
        "Tečaj",
        "Oddelek",
        "Stroškovni nosilec",
        "Opomba",
        "Protikonto",
        "Datum DDV",
        "Tuj dokument",
        "Status",
        "Naziv statusa",
        "Vrednost Debet",
        "Vrednost Kredit",
    };

    private TemeljnicaHrSiExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new TemeljnicaHrSiExportFormatter();
    }

    [Test]
    public void Format_WritesHeaderRow_WithAllTwentyTwoColumnsInOrder()
    {
        var sheet = OpenSheet(BuildData(_ => { }));

        ExpectedHeaders.Length.ShouldBe(22);

        for (var i = 0; i < ExpectedHeaders.Length; i++)
        {
            sheet.Cell(1, i + 1).GetString().ShouldBe(ExpectedHeaders[i]);
        }
    }

    [Test]
    public void Format_WorkEntry_WritesJournalNumberAccountAmountAndDate()
    {
        var sheet = OpenSheet(BuildData(o =>
        {
            o.WorkEntries[0].EmployeeIdNumber = 77;
            o.WorkEntries[0].WorkTime = 8m;
            o.WorkEntries[0].Surcharges = 2m;
        }));

        sheet.Cell(2, 1).GetValue<int>().ShouldBe(1);
        sheet.Cell(2, 2).GetDateTime().ShouldBe(new DateTime(2026, 1, 10));
        sheet.Cell(2, 3).GetString().ShouldBe("77");
        sheet.Cell(2, 4).GetString().ShouldBe("Acme");
        sheet.Cell(2, 5).GetValue<decimal>().ShouldBe(10m);
        sheet.Cell(2, 6).IsEmpty().ShouldBeTrue();
        sheet.Cell(2, 16).GetString().ShouldBe("3600");
    }

    [Test]
    public void Format_TaxableExpense_AddsBookingLine_WithTaxableCounterAccount()
    {
        var sheet = OpenSheet(BuildData(o => o.WorkEntries[0].Expenses =
        [
            new ExpensesExportEntry { Amount = 45.5m, Description = "Travel", Taxable = true },
        ]));

        sheet.Cell(3, 1).GetValue<int>().ShouldBe(2);
        sheet.Cell(3, 5).GetValue<decimal>().ShouldBe(45.5m);
        sheet.Cell(3, 15).GetString().ShouldBe("Travel - Worker One");
        sheet.Cell(3, 16).GetString().ShouldBe("7600");
    }

    [Test]
    public void Format_NonTaxableExpense_AddsBookingLine_WithNonTaxableCounterAccount()
    {
        var sheet = OpenSheet(BuildData(o => o.WorkEntries[0].Expenses =
        [
            new ExpensesExportEntry { Amount = 12m, Description = "Refund", Taxable = false },
        ]));

        sheet.Cell(3, 5).GetValue<decimal>().ShouldBe(12m);
        sheet.Cell(3, 16).GetString().ShouldBe("7800");
    }

    [Test]
    public void Format_Amount_IsRoundedToTwoDecimalPlaces()
    {
        var sheet = OpenSheet(BuildData(o =>
        {
            o.WorkEntries[0].WorkTime = 8.126m;
            o.WorkEntries[0].Surcharges = 0m;
        }));

        sheet.Cell(2, 5).GetValue<decimal>().ShouldBe(8.13m);
    }

    [Test]
    public void Format_ExposesFormatKeyContentTypeAndExtension()
    {
        _formatter.FormatKey.ShouldBe(ExportConstants.FormatTemeljnicaHrSi);
        _formatter.ContentType.ShouldBe(ExportConstants.ContentTypeXlsx);
        _formatter.FileExtension.ShouldBe(".xlsx");
    }

    private IXLWorksheet OpenSheet(OrderExportData data)
    {
        var content = _formatter.Format(data, new ExportOptions { CurrencyCode = "EUR" });
        using var stream = new MemoryStream(content);
        var workbook = new XLWorkbook(stream);
        return workbook.Worksheet(1);
    }

    private static OrderExportData BuildData(Action<OrderGroup> customize)
    {
        var order = new OrderGroup
        {
            OrderShiftId = Guid.NewGuid(),
            OrderName = "Night Watch",
            OrderAbbreviation = "NW",
            CustomerName = "Acme",
            WorkEntries =
            [
                new WorkExportEntry
                {
                    WorkId = Guid.NewGuid(),
                    EmployeeId = Guid.NewGuid(),
                    EmployeeName = "Worker One",
                    EmployeeIdNumber = 1,
                    WorkDate = new DateOnly(2026, 1, 10),
                    StartTime = new TimeOnly(8, 0),
                    EndTime = new TimeOnly(16, 0),
                    WorkTime = 8m,
                    Surcharges = 0m,
                },
            ],
        };

        customize(order);

        return new OrderExportData
        {
            Orders = [order],
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 31),
        };
    }
}
