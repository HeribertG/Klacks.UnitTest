// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for Sie4bSeExportFormatter verifying the SIE 4B tagged-text structure: the
/// #FORMAT PC8/#SIETYP 4 header, one #VER block with exactly two #TRANS lines per work entry
/// and per expense, balanced debit/credit amounts, and CP437 encoding.
/// </summary>
using System.Text;
using Shouldly;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class Sie4bSeExportFormatterTests
{
    private const int Pc8CodePage = 437;

    private Sie4bSeExportFormatter _formatter = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [SetUp]
    public void Setup()
    {
        _formatter = new Sie4bSeExportFormatter();
    }

    [Test]
    public void Format_ContainsFormatPc8Tag()
    {
        var text = FormatText(BuildData(_ => { }));

        text.ShouldContain("#FORMAT PC8");
    }

    [Test]
    public void Format_ContainsSieType4Tag()
    {
        var text = FormatText(BuildData(_ => { }));

        text.ShouldContain("#SIETYP 4");
    }

    [Test]
    public void Format_ContainsFlaggaAndFnamnHeaderTags()
    {
        var text = FormatText(BuildData(_ => { }), companyName: "Test AB");

        text.ShouldContain("#FLAGGA 0");
        text.ShouldContain("#FNAMN \"Test AB\"");
    }

    [Test]
    public void Format_UsesCp437Encoding_NotWindows1252()
    {
        var options = new ExportOptions { Company = new CompanyInfo { Name = "Åström AB" } };
        var bytes = _formatter.Format(BuildData(_ => { }), options);

        var expectedCp437Bytes = Encoding.GetEncoding(Pc8CodePage).GetBytes("#FNAMN \"Åström AB\"");
        var windows1252BytesForSameText = Encoding.GetEncoding(1252).GetBytes("#FNAMN \"Åström AB\"");
        expectedCp437Bytes.ShouldNotBe(windows1252BytesForSameText);

        var containsCp437Encoding = ContainsSubsequence(bytes, expectedCp437Bytes);
        containsCp437Encoding.ShouldBeTrue();

        var roundTripped = Encoding.GetEncoding(Pc8CodePage).GetString(bytes);
        roundTripped.ShouldContain("Åström AB");
    }

    private static bool ContainsSubsequence(byte[] haystack, byte[] needle)
    {
        for (var i = 0; i <= haystack.Length - needle.Length; i++)
        {
            var match = true;
            for (var j = 0; j < needle.Length; j++)
            {
                if (haystack[i + j] != needle[j])
                {
                    match = false;
                    break;
                }
            }

            if (match)
            {
                return true;
            }
        }

        return false;
    }

    [Test]
    public void Format_EmitsOneVerBlockPerWorkEntry_WithExactlyTwoTransLines()
    {
        var lines = FormatLines(BuildData(_ => { }));

        var verIndex = Array.FindIndex(lines, l => l.StartsWith("#VER", StringComparison.Ordinal));
        verIndex.ShouldBeGreaterThanOrEqualTo(0);

        lines[verIndex + 1].ShouldBe("{");
        lines[verIndex + 2].ShouldStartWith("#TRANS");
        lines[verIndex + 3].ShouldStartWith("#TRANS");
        lines[verIndex + 4].ShouldBe("}");
    }

    [Test]
    public void Format_WritesBalancedDebitAndCreditAmounts()
    {
        var lines = FormatLines(BuildData(o =>
        {
            o.WorkEntries[0].WorkTime = 8m;
            o.WorkEntries[0].Surcharges = 1.5m;
        }));

        var verIndex = Array.FindIndex(lines, l => l.StartsWith("#VER", StringComparison.Ordinal));
        var debitLine = lines[verIndex + 2];
        var creditLine = lines[verIndex + 3];

        debitLine.ShouldBe("#TRANS 7010 {} 9.50");
        creditLine.ShouldBe("#TRANS 2890 {} -9.50");
    }

    [Test]
    public void Format_IncludesWorkChangesInAmount()
    {
        var lines = FormatLines(BuildData(o =>
        {
            o.WorkEntries[0].WorkTime = 8m;
            o.WorkEntries[0].Surcharges = 0m;
            o.WorkEntries[0].Changes =
            [
                new WorkChangeExportEntry { ChangeTime = 2m, Surcharges = 0.5m },
            ];
        }));

        var verIndex = Array.FindIndex(lines, l => l.StartsWith("#VER", StringComparison.Ordinal));
        lines[verIndex + 2].ShouldBe("#TRANS 7010 {} 10.50");
    }

    [Test]
    public void Format_EmitsAdditionalVerBlock_PerExpense_UsingTaxableOrNonTaxableAccount()
    {
        var lines = FormatLines(BuildData(o => o.WorkEntries[0].Expenses =
        [
            new ExpensesExportEntry { Amount = 12m, Description = "Travel", Taxable = true },
            new ExpensesExportEntry { Amount = 5m, Description = "Toll", Taxable = false },
        ]));

        var verIndices = new List<int>();
        for (var i = 0; i < lines.Length; i++)
        {
            if (lines[i].StartsWith("#VER", StringComparison.Ordinal))
            {
                verIndices.Add(i);
            }
        }

        verIndices.Count.ShouldBe(3);

        lines[verIndices[1] + 2].ShouldBe("#TRANS 7690 {} 12.00");
        lines[verIndices[1] + 3].ShouldBe("#TRANS 2890 {} -12.00");

        lines[verIndices[2] + 2].ShouldBe("#TRANS 7389 {} 5.00");
        lines[verIndices[2] + 3].ShouldBe("#TRANS 2890 {} -5.00");
    }

    [Test]
    public void Format_UsesAscendingVerificationNumbers()
    {
        var lines = FormatLines(BuildData(o => o.WorkEntries[0].Expenses =
        [
            new ExpensesExportEntry { Amount = 12m, Description = "Travel", Taxable = true },
        ]));

        var verLines = Array.FindAll(lines, l => l.StartsWith("#VER", StringComparison.Ordinal));

        verLines[0].ShouldBe("#VER A 1 20260110 \"Worker One - Night Watch\"");
        verLines[1].ShouldBe("#VER A 2 20260110 \"Travel - Worker One\"");
    }

    private string FormatText(OrderExportData data, string companyName = "")
    {
        var options = new ExportOptions { Company = new CompanyInfo { Name = companyName } };
        var bytes = _formatter.Format(data, options);
        return Encoding.GetEncoding(Pc8CodePage).GetString(bytes);
    }

    private string[] FormatLines(OrderExportData data, string companyName = "")
    {
        return FormatText(data, companyName)
            .Split('\n', StringSplitOptions.RemoveEmptyEntries);
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
