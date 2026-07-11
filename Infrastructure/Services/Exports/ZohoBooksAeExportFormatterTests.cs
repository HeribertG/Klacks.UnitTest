// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ZohoBooksAeExportFormatter verifying the Manual-Journals column layout, that every
/// journal balances (sum of Debit equals sum of Credit), that Debit/Credit are two separate columns,
/// that the two lines of one booking share a Journal#, and that free-text is formula-injection safe.
/// </summary>
using System.Globalization;
using System.Text;
using Shouldly;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class ZohoBooksAeExportFormatterTests
{
    private ZohoBooksAeExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new ZohoBooksAeExportFormatter();
    }

    [Test]
    public void FormatKey_And_ContentType_AreZohoCsv()
    {
        _formatter.FormatKey.ShouldBe(ExportConstants.FormatZohoBooksAe);
        _formatter.ContentType.ShouldBe(ExportConstants.ContentTypeCsv);
        _formatter.FileExtension.ShouldBe(".csv");
    }

    [Test]
    public void Format_EmitsHeader_WithElevenZohoColumns()
    {
        var lines = FormatLines(BuildData(_ => { }));

        lines[0].ShouldBe("Journal Date,Journal#,Reference#,Notes,Account Name,Description,Debit,Credit,Currency Code,Tax Name,Tax Percentage");
    }

    [Test]
    public void Format_EmitsBalancedJournal_DebitEqualsCredit()
    {
        var lines = FormatLines(BuildData(_ => { }));

        decimal totalDebit = 0m;
        decimal totalCredit = 0m;
        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(',');
            totalDebit += ParseCell(cells[6]);
            totalCredit += ParseCell(cells[7]);
        }

        totalDebit.ShouldBe(8.50m);
        totalCredit.ShouldBe(totalDebit);
    }

    [Test]
    public void Format_UsesTwoSeparateColumns_ExactlyOneSidePerLine()
    {
        var lines = FormatLines(BuildData(_ => { }));

        foreach (var line in lines.Skip(1))
        {
            var cells = line.Split(',');
            var hasDebit = !string.IsNullOrEmpty(cells[6]);
            var hasCredit = !string.IsNullOrEmpty(cells[7]);
            (hasDebit ^ hasCredit).ShouldBeTrue($"Line must have exactly one of Debit/Credit: {line}");
        }
    }

    [Test]
    public void Format_TwoLinesOfOneBooking_ShareJournalNumber_AndIsoDate()
    {
        var lines = FormatLines(BuildData(_ => { }));

        var debitLine = lines[1].Split(',');
        var creditLine = lines[2].Split(',');

        debitLine[1].ShouldBe("1");
        creditLine[1].ShouldBe("1");
        debitLine[0].ShouldBe("2026-01-10");
        debitLine[6].ShouldBe("8.50");
        creditLine[7].ShouldBe("8.50");
    }

    [Test]
    public void Format_ExpenseBecomesOwnBalancedJournal()
    {
        var lines = FormatLines(BuildData(o => o.WorkEntries[0].Expenses =
        [
            new ExpensesExportEntry { Amount = 12m, Taxable = true, Description = "Taxi" },
        ]));

        var expenseDebit = lines.Skip(1).Select(l => l.Split(',')).First(c => c[1] == "2" && !string.IsNullOrEmpty(c[6]));
        var expenseCredit = lines.Skip(1).Select(l => l.Split(',')).First(c => c[1] == "2" && !string.IsNullOrEmpty(c[7]));

        expenseDebit[6].ShouldBe("12.00");
        expenseCredit[7].ShouldBe("12.00");
    }

    [Test]
    public void Format_NeutralizesFormulaInjection_InDescription()
    {
        var text = Encoding.UTF8.GetString(_formatter.Format(BuildData(o => o.WorkEntries[0].EmployeeName = "=cmd"), SampleOptions()));

        text.ShouldContain("'=cmd");
    }

    private static decimal ParseCell(string cell)
    {
        return string.IsNullOrEmpty(cell) ? 0m : decimal.Parse(cell, CultureInfo.InvariantCulture);
    }

    private string[] FormatLines(OrderExportData data)
    {
        var bytes = _formatter.Format(data, SampleOptions());
        return Encoding.UTF8.GetString(bytes).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

    private static ExportOptions SampleOptions() => new() { CurrencyCode = "AED" };

    private static OrderExportData BuildData(Action<OrderGroup> customize)
    {
        var order = new OrderGroup
        {
            OrderShiftId = Guid.NewGuid(),
            OrderName = "Night Watch",
            OrderAbbreviation = "NW",
            CustomerId = Guid.NewGuid(),
            CustomerNumber = 77,
            CustomerName = "Muster Anna",
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
                    Surcharges = 0.5m,
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
