// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for OmegaSkExportFormatter verifying the R00/R01/R02 record structure,
/// tab delimiter, fixed column counts, item type 0 for all items and Windows-1250 encoding.
/// </summary>
using System.Text;
using Shouldly;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class OmegaSkExportFormatterTests
{
    private const char Tab = '\t';
    private const int DocumentHeaderColumnCount = 29;
    private const int ItemColumnCount = 25;
    private const int ItemTypeColumn = 1;
    private const int ItemWorkerNumberColumn = 24;
    private const int DocumentHeaderIssueDateColumn = 9;

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
    public void Format_StartsWithDataTypeRecord()
    {
        var lines = FormatLines(BuildData(_ => { }));

        lines[0].ShouldBe("R00\tT00");
    }

    [Test]
    public void Format_WritesOneDocumentHeaderRecord_PerOrder()
    {
        var lines = FormatLines(BuildData(_ => { }));

        lines.Count(l => l.StartsWith("R01\t")).ShouldBe(1);
    }

    [Test]
    public void Format_WritesDocumentHeader_WithFixedColumnCount()
    {
        var lines = FormatLines(BuildData(_ => { }));

        lines.Single(l => l.StartsWith("R01\t")).Split(Tab).Length.ShouldBe(DocumentHeaderColumnCount);
    }

    [Test]
    public void Format_WritesAccountingEntryItem_PerWorkEntry()
    {
        var lines = FormatLines(BuildData(_ => { }));

        var itemLines = lines.Where(l => l.StartsWith("R02\t")).ToArray();
        itemLines.Length.ShouldBe(1);
        itemLines[0].Split(Tab).Length.ShouldBe(ItemColumnCount);
        itemLines[0].Split(Tab)[ItemTypeColumn].ShouldBe("0");
    }

    [Test]
    public void Format_WritesWorkerInternalNumber_AsEmployeeIdNumber()
    {
        var lines = FormatLines(BuildData(o => o.WorkEntries[0].EmployeeIdNumber = 77));

        var itemLine = lines.Single(l => l.StartsWith("R02\t"));
        itemLine.Split(Tab)[ItemWorkerNumberColumn].ShouldBe("77");
    }

    [Test]
    public void Format_WritesNonTaxableExpense_AsAccountingEntryItemType()
    {
        var lines = FormatLines(BuildData(o => o.WorkEntries[0].Expenses =
        [
            new ExpensesExportEntry { Amount = 12m, Description = "Train ticket", Taxable = false },
        ]));

        var expenseLine = lines.Where(l => l.StartsWith("R02\t")).ElementAt(1);
        expenseLine.Split(Tab)[ItemTypeColumn].ShouldBe("0");
    }

    [Test]
    public void Format_WritesTaxableAllowance_AsAccountingEntryItemType()
    {
        var lines = FormatLines(BuildData(o => o.WorkEntries[0].Expenses =
        [
            new ExpensesExportEntry { Amount = 12m, Description = "Allowance", Taxable = true },
        ]));

        var expenseLine = lines.Where(l => l.StartsWith("R02\t")).ElementAt(1);
        expenseLine.Split(Tab)[ItemTypeColumn].ShouldBe("0");
    }

    [Test]
    public void Format_NeverWritesPaymentOrReductionItemType_ForExpenses()
    {
        var lines = FormatLines(BuildData(o => o.WorkEntries[0].Expenses =
        [
            new ExpensesExportEntry { Amount = 12m, Description = "Train ticket", Taxable = false },
            new ExpensesExportEntry { Amount = 5m, Description = "Allowance", Taxable = true },
        ]));

        lines.Where(l => l.StartsWith("R02\t")).ShouldAllBe(l => l.Split(Tab)[ItemTypeColumn] == "0");
    }

    [Test]
    public void Format_WritesIssueDate_AsDdMmYyyy_InDocumentHeader()
    {
        var lines = FormatLines(BuildData(_ => { }));

        var headerLine = lines.Single(l => l.StartsWith("R01\t"));
        headerLine.Split(Tab)[DocumentHeaderIssueDateColumn].ShouldBe("10.01.2026");
    }

    private string[] FormatLines(OrderExportData data)
    {
        var bytes = _formatter.Format(data, new ExportOptions { CurrencyCode = "EUR" });
        return Encoding.GetEncoding(1250).GetString(bytes)
            .Split(ExportConstants.LineEnding, StringSplitOptions.RemoveEmptyEntries);
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
