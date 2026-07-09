// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MoveinIlExportFormatter verifying the 180-character fixed-width detailed-method
/// layout: exact line length, field positions (date, currency, text, accounts, amounts) and CRLF.
/// </summary>
using System.Text;
using Shouldly;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class MoveinIlExportFormatterTests
{
    private MoveinIlExportFormatter _formatter = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [SetUp]
    public void Setup()
    {
        _formatter = new MoveinIlExportFormatter();
    }

    [Test]
    public void Format_ProducesRecordsOf180CharactersIncludingCrLf()
    {
        var bytes = _formatter.Format(BuildData(_ => { }), new ExportOptions { CurrencyCode = "ILS" });
        var text = Encoding.GetEncoding(1255).GetString(bytes);

        text.Length.ShouldBeGreaterThan(0);
        (text.Length % 180).ShouldBe(0);
    }

    [Test]
    public void Format_UsesCrLfLineEndings()
    {
        var bytes = _formatter.Format(BuildData(_ => { }), new ExportOptions { CurrencyCode = "ILS" });
        var text = Encoding.GetEncoding(1255).GetString(bytes);

        text.ShouldEndWith("\r\n");
        text.ShouldNotContain("\n\n");
    }

    [Test]
    public void Format_WritesBelegdatumAndFaelligkeitsdatum_AsDdMmYy()
    {
        var line = FormatLines(BuildData(_ => { }))[0];

        var belegdatum = line.Substring(8, 6);
        var faelligkeitsdatum = line.Substring(19, 6);
        belegdatum.ShouldBe("100126");
        faelligkeitsdatum.ShouldBe("100126");
    }

    [Test]
    public void Format_WritesCurrencyCode_LeftJustifiedThreeChars()
    {
        var line = FormatLines(BuildData(_ => { }), currencyCode: "US")[0];

        line.Substring(25, 3).ShouldBe("US ");
    }

    [Test]
    public void Format_WritesDebitAccount_AsEmployeeIdNumber()
    {
        var line = FormatLines(BuildData(o => o.WorkEntries[0].EmployeeIdNumber = 42))[0];

        line.Substring(50, 8).Trim().ShouldBe("42");
    }

    [Test]
    public void Format_WritesBalancedDebitAndCreditAmounts_AsCentsZeroPadded()
    {
        var line = FormatLines(BuildData(o => { o.WorkEntries[0].WorkTime = 8m; o.WorkEntries[0].Surcharges = 1.5m; }))[0];

        var sollBetrag1 = line.Substring(82, 12);
        var habenBetrag1 = line.Substring(106, 12);
        sollBetrag1.ShouldBe("000000000950");
        habenBetrag1.ShouldBe("000000000950");
    }

    [Test]
    public void Format_WritesZeroForeignCurrencyFields()
    {
        var line = FormatLines(BuildData(_ => { }))[0];

        line.Substring(130, 12).ShouldBe("000000000000");
        line.Substring(142, 12).ShouldBe("000000000000");
        line.Substring(154, 12).ShouldBe("000000000000");
        line.Substring(166, 12).ShouldBe("000000000000");
    }

    [Test]
    public void Format_EmitsOneLinePerExpense_InAdditionToWorkLine()
    {
        var lines = FormatLines(BuildData(o => o.WorkEntries[0].Expenses =
        [
            new ExpensesExportEntry { Amount = 12m, Description = "Travel", Taxable = true },
        ]));

        lines.Length.ShouldBe(2);
    }

    private string[] FormatLines(OrderExportData data, string currencyCode = "ILS")
    {
        var bytes = _formatter.Format(data, new ExportOptions { CurrencyCode = currencyCode });
        return Encoding.GetEncoding(1255).GetString(bytes)
            .Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
    }

    private static OrderExportData BuildData(Action<OrderGroup> customize)
    {
        var order = new OrderGroup
        {
            OrderShiftId = Guid.NewGuid(),
            OrderName = "Night Watch",
            OrderAbbreviation = "NW",
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
