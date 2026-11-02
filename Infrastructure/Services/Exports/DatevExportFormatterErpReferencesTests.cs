// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for DatevExportFormatter verifying that ERP references are carried in the
/// Auftragsnummer column and the Beleginfo type/content pairs, and stay empty otherwise.
/// </summary>
using System.Text;
using Shouldly;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Services.Exports;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class DatevExportFormatterErpReferencesTests
{
    private const int BeleginfoType1Index = 20;
    private const int BeleginfoContent1Index = 21;
    private const int BeleginfoType2Index = 22;
    private const int BeleginfoContent2Index = 23;
    private const int OrderNumberIndex = 94;
    private const int OrderAbbreviationIndex = 10;
    private const int ConsultantHeaderIndex = 10;
    private const int ClientHeaderIndex = 11;

    private DatevExportFormatter _formatter = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [SetUp]
    public void Setup()
    {
        _formatter = new DatevExportFormatter();
    }

    [Test]
    public void Format_WritesErpReferences_IntoOrderNumberAndBeleginfoColumns()
    {
        var data = BuildData(order =>
        {
            order.SourceSystemId = "erp-main";
            order.ExternalOrderReference = "PO-4711";
            order.CustomerExternalReference = "CUST-42";
        });

        var fields = ParseFirstDataRow(data);

        fields[OrderNumberIndex].ShouldBe("\"PO-4711\"");
        fields[BeleginfoType1Index].ShouldBe("\"SourceSystem\"");
        fields[BeleginfoContent1Index].ShouldBe("\"erp-main\"");
        fields[BeleginfoType2Index].ShouldBe("\"ErpCustomerRef\"");
        fields[BeleginfoContent2Index].ShouldBe("\"CUST-42\"");
    }

    [Test]
    public void Format_LeavesReferenceColumnsEmpty_WhenNoErpReferences()
    {
        var data = BuildData(_ => { });

        var fields = ParseFirstDataRow(data);

        fields[OrderNumberIndex].ShouldBeEmpty();
        fields[BeleginfoType1Index].ShouldBeEmpty();
        fields[BeleginfoContent1Index].ShouldBeEmpty();
        fields[BeleginfoType2Index].ShouldBeEmpty();
        fields[BeleginfoContent2Index].ShouldBeEmpty();
    }

    [Test]
    public void Format_StripsQuotes_FromOrderAbbreviation_BecauseBelegfeld1AllowsOnlyRestrictedCharacters()
    {
        var data = BuildData(order => order.OrderAbbreviation = "A\"B");

        var fields = ParseFirstDataRow(data);

        fields[OrderAbbreviationIndex].ShouldBe("\"AB\"");
    }

    [Test]
    public void Format_RemovesFormulaTrigger_FromOrderAbbreviation_BecauseBelegfeld1AllowsOnlyRestrictedCharacters()
    {
        var data = BuildData(order => order.OrderAbbreviation = "=danger");

        var fields = ParseFirstDataRow(data);

        fields[OrderAbbreviationIndex].ShouldBe("\"danger\"");
    }

    [TestCase("1001", "10001", "1001", "10001")]
    [TestCase("9999999", "99999", "9999999", "99999")]
    [TestCase(" 12345 ", " 1 ", "12345", "1")]
    public void Format_WritesConsultantAndClientNumber_IntoHeaderFields11And12(
        string consultant, string client, string expectedConsultant, string expectedClient)
    {
        var header = ParseHeader(BuildData(_ => { }), consultant, client);

        header[ConsultantHeaderIndex].ShouldBe(expectedConsultant);
        header[ClientHeaderIndex].ShouldBe(expectedClient);
    }

    [TestCase(null, null)]
    [TestCase("", "")]
    [TestCase("999", "0")]
    [TestCase("1000", "0")]
    [TestCase("10000000", "100000")]
    [TestCase("12A45", "1.5")]
    [TestCase("-1001", "-1")]
    [TestCase("+1001", "+1")]
    public void Format_LeavesConsultantAndClientEmpty_WhenMissingNonNumericOrOutOfRange(string? consultant, string? client)
    {
        var header = ParseHeader(BuildData(_ => { }), consultant, client);

        header[ConsultantHeaderIndex].ShouldBeEmpty();
        header[ClientHeaderIndex].ShouldBeEmpty();
    }

    [Test]
    public void DecorateFileName_PrefixesExtf_OnlyOnce()
    {
        _formatter.DecorateFileName("order-export_NW.csv").ShouldBe("EXTF_order-export_NW.csv");
    }

    [Test]
    public void ExportFileNameDecorator_AppliesPrefix_OnlyForFormattersWithFileNameProvider()
    {
        ExportFileNameDecorator.Apply(_formatter, "order-export_NW.csv").ShouldBe("EXTF_order-export_NW.csv");
        ExportFileNameDecorator.Apply(new BmdExportFormatter(), "order-export_NW.csv").ShouldBe("order-export_NW.csv");
    }

    private string[] ParseHeader(OrderExportData data, string? consultant, string? client)
    {
        var options = new ExportOptions
        {
            CurrencyCode = "EUR",
            Company = new CompanyInfo { DatevConsultantNumber = consultant, DatevClientNumber = client },
        };
        var bytes = _formatter.Format(data, options);
        var lines = Encoding.GetEncoding(1252).GetString(bytes)
            .Split(ExportConstants.LineEnding, StringSplitOptions.RemoveEmptyEntries);
        return lines[0].Split(';');
    }

    private string[] ParseFirstDataRow(OrderExportData data)
    {
        var bytes = _formatter.Format(data, new ExportOptions { CurrencyCode = "EUR" });
        var lines = Encoding.GetEncoding(1252).GetString(bytes)
            .Split(ExportConstants.LineEnding, StringSplitOptions.RemoveEmptyEntries);
        return lines[2].Split(';');
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
            CustomerName = "Muster, Anna",
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
