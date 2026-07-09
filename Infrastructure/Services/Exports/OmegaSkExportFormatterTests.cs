// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for OmegaSkExportFormatter verifying the R00/R01/R02 record structure,
/// semicolon delimiter, item-type codes and Windows-1250 encoding.
/// </summary>
using System.Text;
using Shouldly;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class OmegaSkExportFormatterTests
{
    private OmegaSkExportFormatter _formatter = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [SetUp]
    public void Setup()
    {
        _formatter = new OmegaSkExportFormatter();
    }

    [Test]
    public void Format_StartsWithFormatHeaderRecord()
    {
        var lines = FormatLines(BuildData(_ => { }));

        lines[0].ShouldStartWith("R00;OMEGA;");
    }

    [Test]
    public void Format_WritesOneDocumentHeaderRecord_PerOrder()
    {
        var lines = FormatLines(BuildData(_ => { }));

        lines.Count(l => l.StartsWith("R01;")).ShouldBe(1);
    }

    [Test]
    public void Format_WritesAccountingEntryItem_PerWorkEntry()
    {
        var lines = FormatLines(BuildData(_ => { }));

        var itemLines = lines.Where(l => l.StartsWith("R02;")).ToArray();
        itemLines.Length.ShouldBe(1);
        itemLines[0].Split(';')[1].ShouldBe("0");
    }

    [Test]
    public void Format_WritesWorkerInternalNumber_AsEmployeeIdNumber()
    {
        var lines = FormatLines(BuildData(o => o.WorkEntries[0].EmployeeIdNumber = 77));

        var itemLine = lines.Single(l => l.StartsWith("R02;"));
        itemLine.Split(';')[2].ShouldBe("77");
    }

    [Test]
    public void Format_WritesTaxableExpense_AsReimbursementItemType()
    {
        var lines = FormatLines(BuildData(o => o.WorkEntries[0].Expenses =
        [
            new ExpensesExportEntry { Amount = 12m, Description = "Travel", Taxable = true },
        ]));

        var expenseLine = lines.Where(l => l.StartsWith("R02;")).ElementAt(1);
        expenseLine.Split(';')[1].ShouldBe("1");
    }

    [Test]
    public void Format_WritesNonTaxableExpense_AsReductionItemType()
    {
        var lines = FormatLines(BuildData(o => o.WorkEntries[0].Expenses =
        [
            new ExpensesExportEntry { Amount = 12m, Description = "Refund", Taxable = false },
        ]));

        var expenseLine = lines.Where(l => l.StartsWith("R02;")).ElementAt(1);
        expenseLine.Split(';')[1].ShouldBe("2");
    }

    [Test]
    public void Format_WritesDates_AsDdMmYyyy()
    {
        var lines = FormatLines(BuildData(_ => { }));

        var itemLine = lines.Single(l => l.StartsWith("R02;"));
        itemLine.Split(';')[3].ShouldBe("10.01.2026");
    }

    private string[] FormatLines(OrderExportData data)
    {
        var bytes = _formatter.Format(data, new ExportOptions { CurrencyCode = "EUR" });
        return Encoding.GetEncoding(1250).GetString(bytes)
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
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
