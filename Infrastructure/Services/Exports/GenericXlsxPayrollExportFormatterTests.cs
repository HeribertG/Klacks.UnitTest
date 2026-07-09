// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the generic Tier-A Excel payroll formatter: header row, real Excel dates,
/// wage-type mapping, surcharge gating and unmapped-absence skipping.
/// </summary>

using ClosedXML.Excel;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Services.Exports;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class GenericXlsxPayrollExportFormatterTests
{
    private GenericXlsxPayrollExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new GenericXlsxPayrollExportFormatter();
    }

    private static PayrollExportGroupConfig Config(
        string baseWageType = "BASE",
        string surchargeWageType = "",
        string absenceMappingJson = "{}")
    {
        return new PayrollExportGroupConfig
        {
            GroupId = Guid.NewGuid(),
            TargetSystem = PayrollExportConstants.FormatKeyGenericPayrollXlsx,
            BaseWageType = baseWageType,
            SurchargeWageType = surchargeWageType,
            AbsenceMappingJson = absenceMappingJson,
        };
    }

    private static PayrollExportData DataWith(params PayrollDayEntry[] entries)
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
                    FullName = "Muster, Max",
                    Entries = entries.ToList(),
                },
            ],
        };
    }

    private static IXLWorksheet OpenSheet(byte[] content)
    {
        using var stream = new MemoryStream(content);
        var workbook = new XLWorkbook(stream);
        return workbook.Worksheet(1);
    }

    [Test]
    public void Format_WritesHeaderRow()
    {
        var result = _formatter.Format(DataWith(), Config());
        var sheet = OpenSheet(result.Content);

        sheet.Cell(1, 1).GetString().ShouldBe("PersonnelNumber");
        sheet.Cell(1, 2).GetString().ShouldBe("Date");
        sheet.Cell(1, 3).GetString().ShouldBe("WageType");
        sheet.Cell(1, 4).GetString().ShouldBe("Quantity");
    }

    [Test]
    public void Format_WorkHours_WritesRealExcelDateAndBaseWageType()
    {
        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.WorkHours,
            Quantity = 8.5m,
        });

        var result = _formatter.Format(data, Config(baseWageType: "1000"));
        var sheet = OpenSheet(result.Content);

        sheet.Cell(2, 1).GetValue<int>().ShouldBe(42);
        sheet.Cell(2, 2).GetDateTime().ShouldBe(new DateTime(2026, 1, 15));
        sheet.Cell(2, 3).GetString().ShouldBe("1000");
        sheet.Cell(2, 4).GetValue<decimal>().ShouldBe(8.5m);
        result.RecordCount.ShouldBe(1);
        result.SkippedAbsenceCount.ShouldBe(0);
    }

    [Test]
    public void Format_Surcharge_IsOmittedWhenNoSurchargeWageTypeConfigured()
    {
        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.Surcharge,
            Quantity = 2m,
        });

        var result = _formatter.Format(data, Config(surchargeWageType: string.Empty));

        result.RecordCount.ShouldBe(0);
    }

    [Test]
    public void Format_Surcharge_IsEmittedWithSurchargeWageTypeWhenConfigured()
    {
        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.Surcharge,
            Quantity = 2m,
        });

        var result = _formatter.Format(data, Config(surchargeWageType: "1500"));
        var sheet = OpenSheet(result.Content);

        result.RecordCount.ShouldBe(1);
        sheet.Cell(2, 3).GetString().ShouldBe("1500");
    }

    [Test]
    public void Format_MappedAbsence_UsesConfiguredWageType()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":{{\"ausfallschluessel\":\"\",\"wageType\":\"2000\"}}}}";

        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 20),
            Kind = PayrollEntryKind.Absence,
            Quantity = 8m,
            AbsenceId = absenceId,
        });

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));
        var sheet = OpenSheet(result.Content);

        result.RecordCount.ShouldBe(1);
        result.SkippedAbsenceCount.ShouldBe(0);
        sheet.Cell(2, 3).GetString().ShouldBe("2000");
    }

    [Test]
    public void Format_UnmappedAbsence_IsSkippedAndCounted()
    {
        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 20),
            Kind = PayrollEntryKind.Absence,
            Quantity = 8m,
            AbsenceId = Guid.NewGuid(),
        });

        var result = _formatter.Format(data, Config(absenceMappingJson: "{}"));

        result.RecordCount.ShouldBe(0);
        result.SkippedAbsenceCount.ShouldBe(1);
    }

    [Test]
    public void Format_ExposesFormatKeyContentTypeAndExtension()
    {
        _formatter.FormatKey.ShouldBe(PayrollExportConstants.FormatKeyGenericPayrollXlsx);
        _formatter.ContentType.ShouldBe(ExportConstants.ContentTypeXlsx);
        _formatter.FileExtension.ShouldBe(".xlsx");
    }
}
