// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for ReportXlsxBuilder: verifies that resolved report values regain their type in the
/// workbook, that grouped sheets get SUBTOTAL formulas over their own rows, and that sheet and
/// file names stay valid.
/// </summary>
namespace Klacks.UnitTest.Infrastructure.Services.Exports;

using ClosedXML.Excel;
using Klacks.Api.Application.DTOs.Reports;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Services.Exports;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class ReportXlsxBuilderTests
{
    private ReportXlsxBuilder _builder = null!;

    [SetUp]
    public void SetUp()
    {
        _builder = new ReportXlsxBuilder();
    }

    private static ReportXlsxColumnResource Column(string header, ReportFieldType type)
    {
        return new ReportXlsxColumnResource { Header = header, Type = (int)type };
    }

    private static XLWorkbook Open(ReportExportResult result)
    {
        return new XLWorkbook(new MemoryStream(result.FileContent));
    }

    [Test]
    public void Build_writes_numbers_dates_and_text_in_their_own_type()
    {
        var request = new ReportXlsxRequest
        {
            FileName = "Stundenrapport",
            Sheets =
            [
                new ReportXlsxSheetResource
                {
                    Name = "Arbeit",
                    Columns =
                    [
                        Column("Datum", ReportFieldType.Date),
                        Column("Schicht", ReportFieldType.Text),
                        Column("Stunden", ReportFieldType.Number),
                    ],
                    Rows = [["12.05.2026", "Frühdienst", "8.50"]],
                },
            ],
        };

        var result = _builder.Build(request);
        using var workbook = Open(result);
        var sheet = workbook.Worksheet("Arbeit");

        sheet.Cell(2, 1).DataType.ShouldBe(XLDataType.DateTime);
        sheet.Cell(2, 1).GetDateTime().ShouldBe(new DateTime(2026, 5, 12));
        sheet.Cell(2, 2).GetString().ShouldBe("Frühdienst");
        sheet.Cell(2, 3).DataType.ShouldBe(XLDataType.Number);
        sheet.Cell(2, 3).GetDouble().ShouldBe(8.5);
    }

    [TestCase("1234.50", 1234.5)]
    [TestCase("1234,50", 1234.5)]
    [TestCase("1'234.50", 1234.5)]
    [TestCase("1.234,50", 1234.5)]
    [TestCase("1,234.50", 1234.5)]
    [TestCase("-8,25", -8.25)]
    [TestCase("0", 0)]
    public void Build_reads_a_number_in_any_separator_convention(string raw, double expected)
    {
        var request = new ReportXlsxRequest
        {
            Sheets =
            [
                new ReportXlsxSheetResource
                {
                    Columns = [Column("Betrag", ReportFieldType.Currency)],
                    Rows = [[raw]],
                },
            ],
        };

        using var workbook = Open(_builder.Build(request));
        workbook.Worksheets.First().Cell(2, 1).GetDouble().ShouldBe(expected);
    }

    [Test]
    public void Build_keeps_a_value_that_does_not_match_its_column_type_as_text()
    {
        var request = new ReportXlsxRequest
        {
            Sheets =
            [
                new ReportXlsxSheetResource
                {
                    Columns = [Column("Stunden", ReportFieldType.Number)],
                    Rows = [["keine Angabe"]],
                },
            ],
        };

        using var workbook = Open(_builder.Build(request));
        workbook.Worksheets.First().Cell(2, 1).GetString().ShouldBe("keine Angabe");
    }

    [Test]
    public void Build_adds_a_subtotal_formula_per_group()
    {
        var request = new ReportXlsxRequest
        {
            Sheets =
            [
                new ReportXlsxSheetResource
                {
                    Name = "Arbeit",
                    Columns = [Column("Schicht", ReportFieldType.Text), Column("Stunden", ReportFieldType.Number)],
                    Rows =
                    [
                        ["Früh", "8.00"],
                        ["Früh", "7.00"],
                        ["Spät", "6.00"],
                    ],
                    GroupColumnIndex = 0,
                    Subtotals = true,
                },
            ],
        };

        using var workbook = Open(_builder.Build(request));
        var sheet = workbook.Worksheet("Arbeit");

        // rows 2 and 3 are the first group, row 4 its subtotal
        sheet.Cell(4, 1).GetString().ShouldBe("Σ Früh");
        sheet.Cell(4, 2).FormulaA1.ShouldBe("SUBTOTAL(9,B2:B3)");

        // row 5 is the only row of the second group, row 6 its subtotal
        sheet.Cell(6, 1).GetString().ShouldBe("Σ Spät");
        sheet.Cell(6, 2).FormulaA1.ShouldBe("SUBTOTAL(9,B5:B5)");
    }

    [Test]
    public void Build_groups_rows_without_subtotals_when_they_are_not_requested()
    {
        var request = new ReportXlsxRequest
        {
            Sheets =
            [
                new ReportXlsxSheetResource
                {
                    Columns = [Column("Schicht", ReportFieldType.Text), Column("Stunden", ReportFieldType.Number)],
                    Rows = [["Früh", "8.00"], ["Spät", "6.00"]],
                    GroupColumnIndex = 0,
                    Subtotals = false,
                },
            ],
        };

        using var workbook = Open(_builder.Build(request));
        var sheet = workbook.Worksheets.First();

        sheet.Cell(4, 1).IsEmpty().ShouldBeTrue();
    }

    [Test]
    public void Build_ignores_a_group_column_outside_the_columns()
    {
        var request = new ReportXlsxRequest
        {
            Sheets =
            [
                new ReportXlsxSheetResource
                {
                    Columns = [Column("Schicht", ReportFieldType.Text)],
                    Rows = [["Früh"], ["Spät"]],
                    GroupColumnIndex = 7,
                    Subtotals = true,
                },
            ],
        };

        using var workbook = Open(_builder.Build(request));
        var sheet = workbook.Worksheets.First();

        sheet.Cell(2, 1).GetString().ShouldBe("Früh");
        sheet.Cell(3, 1).GetString().ShouldBe("Spät");
        sheet.Cell(4, 1).IsEmpty().ShouldBeTrue();
    }

    [Test]
    public void Build_makes_sheet_names_valid_and_unique()
    {
        var request = new ReportXlsxRequest
        {
            Sheets =
            [
                new ReportXlsxSheetResource { Name = "Arbeit/Spesen", Columns = [Column("A", ReportFieldType.Text)] },
                new ReportXlsxSheetResource { Name = "ArbeitSpesen", Columns = [Column("A", ReportFieldType.Text)] },
                new ReportXlsxSheetResource { Name = string.Empty, Columns = [Column("A", ReportFieldType.Text)] },
            ],
        };

        using var workbook = Open(_builder.Build(request));

        workbook.Worksheets.Count.ShouldBe(3);
        workbook.Worksheets.Select(w => w.Name).ShouldBe(["ArbeitSpesen", "ArbeitSpesen (2)", "Report"]);
    }

    [Test]
    public void Build_appends_the_extension_to_the_file_name()
    {
        _builder.Build(new ReportXlsxRequest { FileName = "Rapport" }).FileName.ShouldBe("Rapport.xlsx");
        _builder.Build(new ReportXlsxRequest { FileName = "Rapport.xlsx" }).FileName.ShouldBe("Rapport.xlsx");
        _builder.Build(new ReportXlsxRequest { FileName = "  " }).FileName.ShouldBe("report.xlsx");
    }

    [Test]
    public void Build_produces_a_workbook_even_without_sheets()
    {
        var result = _builder.Build(new ReportXlsxRequest());

        result.FileContent.Length.ShouldBeGreaterThan(0);
        using var workbook = Open(result);
        workbook.Worksheets.Count.ShouldBe(1);
    }
}
