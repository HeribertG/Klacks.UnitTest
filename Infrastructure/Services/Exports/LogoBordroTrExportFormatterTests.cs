// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for LogoBordroTrExportFormatter verifying the Logo Bordro Plus "Puantaj" three-row
/// structure (row 1 labels, row 2 reference codes, row 3+ data), that wage-type columns and their
/// row-2 codes come from the group config, that the Sicil No code is the documented "00001" while
/// the Ad/Soyad codes are left empty, per-employee wage aggregation, name splitting, and that
/// unmapped absences are skipped rather than emitted.
/// </summary>
using System.IO;
using ClosedXML.Excel;
using Shouldly;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class LogoBordroTrExportFormatterTests
{
    private const string BaseWageType = "1000";
    private const string SurchargeWageType = "1010";
    private const string AbsenceWageType = "2000";
    private static readonly Guid MappedAbsenceId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private LogoBordroTrExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new LogoBordroTrExportFormatter();
    }

    [Test]
    public void FormatKey_And_ContentType_AreLogoXlsx()
    {
        _formatter.FormatKey.ShouldBe(PayrollExportConstants.FormatKeyLogoBordroTr);
        _formatter.ContentType.ShouldBe(ExportConstants.ContentTypeXlsx);
        _formatter.FileExtension.ShouldBe(".xlsx");
    }

    [Test]
    public void Format_EmitsThreeRowStructure_WithIdentityHeadersAndCodes()
    {
        var sheet = OpenSheet(_formatter.Format(BuildData(), Config()));

        sheet.Cell(1, 1).GetString().ShouldBe("Sicil No");
        sheet.Cell(1, 2).GetString().ShouldBe("Ad");
        sheet.Cell(1, 3).GetString().ShouldBe("Soyad");

        sheet.Cell(2, 1).GetString().ShouldBe("00001");
        sheet.Cell(2, 2).GetString().ShouldBeEmpty();
        sheet.Cell(2, 3).GetString().ShouldBeEmpty();
    }

    [Test]
    public void Format_WageColumnCodes_ComeFromConfig()
    {
        var sheet = OpenSheet(_formatter.Format(BuildData(), Config()));

        sheet.Cell(2, 4).GetString().ShouldBe(BaseWageType);
        sheet.Cell(2, 5).GetString().ShouldBe(SurchargeWageType);
    }

    [Test]
    public void Format_AggregatesHoursPerEmployeePerWageType()
    {
        var sheet = OpenSheet(_formatter.Format(BuildData(), Config()));

        sheet.Cell(3, 1).GetValue<int>().ShouldBe(42);
        sheet.Cell(3, 2).GetString().ShouldBe("Mehmet");
        sheet.Cell(3, 3).GetString().ShouldBe("Yilmaz");
        sheet.Cell(3, 4).GetValue<decimal>().ShouldBe(16m);
        sheet.Cell(3, 5).GetValue<decimal>().ShouldBe(1.5m);
    }

    [Test]
    public void Format_SplitsCompoundFirstName_OnLastSpace()
    {
        var data = BuildData();
        data.Employees[0].FullName = "Mehmet Ali Kaya";

        var sheet = OpenSheet(_formatter.Format(data, Config()));

        sheet.Cell(3, 2).GetString().ShouldBe("Mehmet Ali");
        sheet.Cell(3, 3).GetString().ShouldBe("Kaya");
    }

    [Test]
    public void Format_SkipsUnmappedAbsence_NotEmitted()
    {
        var data = BuildData();
        data.Employees[0].Entries.Add(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 7),
            Kind = PayrollEntryKind.Absence,
            Quantity = 8m,
            AbsenceId = Guid.NewGuid(),
        });

        var result = _formatter.Format(data, Config());
        var sheet = OpenSheet(result);

        result.SkippedAbsenceCount.ShouldBe(1);
        sheet.Cell(2, 6).GetString().ShouldBeEmpty();
    }

    [Test]
    public void Format_MappedAbsence_BecomesOwnWageColumn()
    {
        var data = BuildData();
        data.Employees[0].Entries.Add(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 8),
            Kind = PayrollEntryKind.Absence,
            Quantity = 8m,
            AbsenceId = MappedAbsenceId,
        });

        var config = Config(
            $"{{\"{MappedAbsenceId}\":{{\"WageType\":\"{AbsenceWageType}\",\"Ausfallschluessel\":\"K\"}}}}");
        var sheet = OpenSheet(_formatter.Format(data, config));

        sheet.Cell(2, 6).GetString().ShouldBe(AbsenceWageType);
        sheet.Cell(3, 6).GetValue<decimal>().ShouldBe(8m);
    }

    private static IXLWorksheet OpenSheet(PayrollExportResult result)
    {
        using var stream = new MemoryStream(result.Content);
        var workbook = new XLWorkbook(stream);
        return workbook.Worksheet(1);
    }

    private static PayrollExportGroupConfig Config(string absenceMappingJson = "{}")
    {
        return new PayrollExportGroupConfig
        {
            GroupId = Guid.NewGuid(),
            TargetSystem = PayrollExportConstants.FormatKeyLogoBordroTr,
            BaseWageType = BaseWageType,
            SurchargeWageType = SurchargeWageType,
            AbsenceMappingJson = absenceMappingJson,
        };
    }

    private static PayrollExportData BuildData()
    {
        return new PayrollExportData
        {
            GroupId = Guid.NewGuid(),
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 31),
            Employees =
            [
                new PayrollEmployee
                {
                    ClientId = Guid.NewGuid(),
                    IdNumber = 42,
                    FullName = "Mehmet Yilmaz",
                    Entries =
                    [
                        new PayrollDayEntry { Date = new DateOnly(2026, 1, 5), Kind = PayrollEntryKind.WorkHours, Quantity = 8m },
                        new PayrollDayEntry { Date = new DateOnly(2026, 1, 6), Kind = PayrollEntryKind.WorkHours, Quantity = 8m },
                        new PayrollDayEntry { Date = new DateOnly(2026, 1, 5), Kind = PayrollEntryKind.Surcharge, Quantity = 1.5m },
                    ],
                },
            ],
        };
    }
}
