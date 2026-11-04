// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for Sie4bSeExportFormatter verifying the SIE 4B tagged-text structure: the
/// #FORMAT PC8/#SIETYP 4 header, only the wages and payables #KONTO declarations (expenses belong to the
/// employee and never go into a customer order export), one #VER block with exactly two #TRANS lines per
/// work entry, balanced debit/credit amounts, and CP437 encoding.
/// </summary>
using System.Text;
using Shouldly;
using Klacks.Api.Domain.Enums;
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
                new WorkChangeExportEntry { Type = WorkChangeType.CorrectionEnd, ChangeTime = 2m, Surcharges = 0.5m },
            ];
        }));

        var verIndex = Array.FindIndex(lines, l => l.StartsWith("#VER", StringComparison.Ordinal));
        lines[verIndex + 2].ShouldBe("#TRANS 7010 {} 10.50");
    }

    [TestCase(WorkChangeType.ReplacementStart)]
    [TestCase(WorkChangeType.ReplacementEnd)]
    [TestCase(WorkChangeType.ReplacementWithin)]
    [TestCase(WorkChangeType.Briefing)]
    [TestCase(WorkChangeType.Debriefing)]
    public void Format_NonHourBearingChange_DoesNotAddToAmount(WorkChangeType type)
    {
        var lines = FormatLines(BuildData(o =>
        {
            o.WorkEntries[0].WorkTime = 8m;
            o.WorkEntries[0].Surcharges = 0m;
            o.WorkEntries[0].Changes =
            [
                new WorkChangeExportEntry { Type = type, ChangeTime = 2m, Surcharges = 0.5m },
            ];
        }));

        var verIndex = Array.FindIndex(lines, l => l.StartsWith("#VER", StringComparison.Ordinal));
        lines[verIndex + 2].ShouldBe("#TRANS 7010 {} 8.00");
    }

    [Test]
    public void Format_DeclaresOnlyWagesAndPayablesAccounts()
    {
        var lines = FormatLines(BuildData(_ => { }));

        Array.FindAll(lines, l => l.StartsWith("#KONTO", StringComparison.Ordinal)).ShouldBe(
        [
            "#KONTO 7010 \"Salaries and wages (placeholder)\"",
            "#KONTO 2890 \"Other short-term liabilities (placeholder)\"",
        ]);
    }

    [Test]
    public void Format_UsesAscendingVerificationNumbers()
    {
        var lines = FormatLines(BuildData(o => o.WorkEntries.Add(new WorkExportEntry
        {
            WorkId = Guid.NewGuid(),
            EmployeeId = Guid.NewGuid(),
            EmployeeName = "Worker Two",
            EmployeeIdNumber = 2,
            WorkDate = new DateOnly(2026, 1, 11),
            StartTime = new TimeOnly(8, 0),
            EndTime = new TimeOnly(12, 0),
            WorkTime = 4m,
        })));

        var verLines = Array.FindAll(lines, l => l.StartsWith("#VER", StringComparison.Ordinal));

        verLines.Length.ShouldBe(2);
        verLines[0].ShouldBe("#VER A 1 20260110 \"Worker One - Night Watch\"");
        verLines[1].ShouldBe("#VER A 2 20260111 \"Worker Two - Night Watch\"");
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
