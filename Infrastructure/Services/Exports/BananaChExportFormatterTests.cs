// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for BananaChExportFormatter verifying the Banana "Text file with column headers"
/// transaction import: NameXml header row, tab delimiter, ISO dates, decimal point amounts, one row
/// per work entry and per expense with placeholder KMU accounts, sanitising of tabs/newlines/quotes
/// in free text, the empty export and UTF-8 encoding without BOM.
/// </summary>
using System.Text;
using Shouldly;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class BananaChExportFormatterTests
{
    private const string Header = "Date\tDoc\tDocOriginal\tDescription\tAccountDebit\tAccountCredit\tAmount";

    private BananaChExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new BananaChExportFormatter();
    }

    [Test]
    public void FormatKey_ContentType_And_Extension_AreBananaText()
    {
        _formatter.FormatKey.ShouldBe(ExportConstants.FormatBananaCh);
        _formatter.FormatKey.ShouldBe("banana-ch");
        _formatter.ContentType.ShouldBe(ExportConstants.ContentTypeOctetStream);
        _formatter.FileExtension.ShouldBe(".txt");
    }

    [Test]
    public void Format_EmitsExactOutput_ForSmallSample()
    {
        var data = BuildData(o =>
        {
            o.ExternalOrderReference = "ERP-42";
            o.WorkEntries[0].Changes =
            [
                new WorkChangeExportEntry { ChangeTime = 1m, Surcharges = 0.25m },
            ];
            o.WorkEntries[0].Expenses =
            [
                new ExpensesExportEntry { Amount = 12.5m, Taxable = true, Description = "Zulage" },
                new ExpensesExportEntry { Amount = 7m, Taxable = false, Description = "Taxi" },
            ];
        });

        var text = FormatText(data);

        text.ShouldBe(
            Header + "\n"
            + "2026-01-10\t1\tERP-42\tWorker One - Night Watch\t5000\t2210\t9.75\n"
            + "2026-01-10\t2\tERP-42\tZulage - Worker One\t5800\t2210\t12.50\n"
            + "2026-01-10\t3\tERP-42\tTaxi - Worker One\t5820\t2210\t7.00\n");
    }

    [Test]
    public void Format_FirstLine_IsNameXmlHeader()
    {
        var lines = FormatLines(BuildData(_ => { }));

        lines[0].ShouldBe(Header);
    }

    [Test]
    public void Format_UsesTabDelimiter_WithSevenFieldsPerLine()
    {
        var lines = FormatLines(BuildData(_ => { }));

        foreach (var line in lines)
        {
            line.Split('\t').Length.ShouldBe(7, $"Unexpected field count: {line}");
            line.ShouldNotContain(";");
        }
    }

    [Test]
    public void Format_UsesIsoDate()
    {
        var cells = FormatLines(BuildData(_ => { }))[1].Split('\t');

        cells[0].ShouldBe("2026-01-10");
    }

    [Test]
    public void Format_UsesDecimalPoint_WithoutThousandsSeparator()
    {
        var cells = FormatLines(BuildData(o => o.WorkEntries[0].Expenses =
        [
            new ExpensesExportEntry { Amount = 1234.567m, Taxable = false, Description = "Hotel" },
        ]))[2].Split('\t');

        cells[6].ShouldBe("1234.57");
    }

    [Test]
    public void Format_LeavesDocOriginalEmpty_WhenNoExternalReference()
    {
        var cells = FormatLines(BuildData(_ => { }))[1].Split('\t');

        cells[1].ShouldBe("1");
        cells[2].ShouldBe(string.Empty);
    }

    [Test]
    public void Format_SanitisesTabsNewlinesAndQuotes_InText()
    {
        var lines = FormatLines(BuildData(o =>
        {
            o.OrderName = "Night\tWatch\r\nNorth \"A\"";
            o.ExternalOrderReference = "REF\t1\n2";
        }));

        lines.Length.ShouldBe(2);
        var cells = lines[1].Split('\t');
        cells.Length.ShouldBe(7);
        cells[2].ShouldBe("REF 1 2");
        cells[3].ShouldBe("Worker One - Night Watch North 'A'");
    }

    [Test]
    public void Format_NeutralizesFormulaInjection_InDescription()
    {
        var cells = FormatLines(BuildData(o => o.WorkEntries[0].EmployeeName = "=cmd"))[1].Split('\t');

        cells[3].ShouldStartWith("'=cmd");
    }

    [Test]
    public void Format_ExpenseRows_UseTaxableAndNonTaxablePlaceholderAccounts()
    {
        var lines = FormatLines(BuildData(o => o.WorkEntries[0].Expenses =
        [
            new ExpensesExportEntry { Amount = 3m, Taxable = true, Description = "Zulage" },
            new ExpensesExportEntry { Amount = 4m, Taxable = false, Description = "Spesen" },
        ]));

        lines.Length.ShouldBe(4);
        var taxable = lines[2].Split('\t');
        var nonTaxable = lines[3].Split('\t');

        taxable[4].ShouldBe("5800");
        taxable[5].ShouldBe("2210");
        taxable[6].ShouldBe("3.00");
        nonTaxable[4].ShouldBe("5820");
        nonTaxable[5].ShouldBe("2210");
        nonTaxable[6].ShouldBe("4.00");
    }

    [Test]
    public void Format_EmptyExport_EmitsHeaderOnly()
    {
        var data = new OrderExportData
        {
            Orders = [],
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 31),
        };

        FormatText(data).ShouldBe(Header + "\n");
    }

    [Test]
    public void Format_IsUtf8WithoutBom_AndKeepsUmlauts()
    {
        var bytes = _formatter.Format(BuildData(o => o.WorkEntries[0].EmployeeName = "Jürg Märki"), new ExportOptions());

        bytes[0].ShouldBe((byte)'D');
        var text = new UTF8Encoding(false, true).GetString(bytes);
        text.ShouldContain("Jürg Märki - Night Watch");
        text.ShouldNotContain("\r");
    }

    private string FormatText(OrderExportData data)
    {
        return Encoding.UTF8.GetString(_formatter.Format(data, new ExportOptions { CurrencyCode = "CHF" }));
    }

    private string[] FormatLines(OrderExportData data)
    {
        return FormatText(data).Split('\n', StringSplitOptions.RemoveEmptyEntries);
    }

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
