// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for BmdExportFormatter verifying that the trailing ERP reference columns
/// (extbelegnr, quellsystem, extkundenref) are declared in the header and populated per row,
/// and that the free-text OrderAbbreviation is escaped (separator quoting + formula neutralisation)
/// so it cannot shift columns or inject spreadsheet formulas.
/// </summary>
using System.Text;
using Shouldly;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class BmdExportFormatterErpReferencesTests
{
    private BmdExportFormatter _formatter = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [SetUp]
    public void Setup()
    {
        _formatter = new BmdExportFormatter();
    }

    [Test]
    public void Format_DeclaresErpReferenceColumns_InHeader()
    {
        var lines = FormatLines(BuildData(_ => { }));

        var headers = lines[0].Split(';');
        headers[^3].ShouldBe("extbelegnr");
        headers[^2].ShouldBe("quellsystem");
        headers[^1].ShouldBe("extkundenref");
    }

    [Test]
    public void Format_WritesErpReferences_IntoTrailingColumns()
    {
        var data = BuildData(order =>
        {
            order.SourceSystemId = "erp-main";
            order.ExternalOrderReference = "PO-4711";
            order.CustomerExternalReference = "CUST-42";
        });

        var lines = FormatLines(data);

        var fields = lines[1].Split(';');
        fields[^3].ShouldBe("PO-4711");
        fields[^2].ShouldBe("erp-main");
        fields[^1].ShouldBe("CUST-42");
    }

    [Test]
    public void Format_LeavesTrailingColumnsEmpty_WhenNoErpReferences()
    {
        var lines = FormatLines(BuildData(_ => { }));

        var fields = lines[1].Split(';');
        fields[^3].ShouldBeEmpty();
        fields[^2].ShouldBeEmpty();
        fields[^1].ShouldBeEmpty();
    }

    [Test]
    public void Format_QuotesOrderAbbreviation_WhenItContainsSeparator()
    {
        var lines = FormatLines(BuildData(order => order.OrderAbbreviation = "A;B"));

        lines[1].ShouldContain("10.01.2026;\"A;B\";ER");
    }

    [Test]
    public void Format_NeutralizesFormulaInjection_InOrderAbbreviation()
    {
        var lines = FormatLines(BuildData(order => order.OrderAbbreviation = "=danger"));

        lines[1].ShouldContain("10.01.2026;'=danger;ER");
    }

    private string[] FormatLines(OrderExportData data)
    {
        var bytes = _formatter.Format(data, new ExportOptions { CurrencyCode = "EUR" });
        return Encoding.GetEncoding(1252).GetString(bytes)
            .Split(ExportConstants.LineEnding, StringSplitOptions.RemoveEmptyEntries);
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
