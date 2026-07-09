// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the generic Tier-A delimited payroll formatter: header row, ISO dates, delimiter/
/// encoding from config, wage-type mapping, surcharge gating and unmapped-absence skipping.
/// </summary>

using System.Text;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Services.Exports;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class GenericDelimitedPayrollExportFormatterTests
{
    private GenericDelimitedPayrollExportFormatter _formatter = null!;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [SetUp]
    public void Setup()
    {
        _formatter = new GenericDelimitedPayrollExportFormatter();
    }

    private static PayrollExportGroupConfig Config(
        string delimiter = ";",
        string encoding = "utf-8",
        string baseWageType = "BASE",
        string surchargeWageType = "",
        string absenceMappingJson = "{}")
    {
        return new PayrollExportGroupConfig
        {
            GroupId = Guid.NewGuid(),
            TargetSystem = PayrollExportConstants.FormatKeyGenericPayrollCsv,
            Delimiter = delimiter,
            Encoding = encoding,
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

    private static string[] Lines(byte[] content, string encoding = "utf-8")
    {
        return Encoding.GetEncoding(encoding).GetString(content)
            .Split(PayrollExportConstants.LineEnding, StringSplitOptions.RemoveEmptyEntries);
    }

    [Test]
    public void Format_WritesHeaderRow_WithFourColumns()
    {
        var result = _formatter.Format(DataWith(), Config());
        var lines = Lines(result.Content);

        lines[0].ShouldBe("PersonnelNumber;Date;WageType;Quantity");
    }

    [Test]
    public void Format_WorkHours_ProducesRowWithIsoDateAndBaseWageType()
    {
        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.WorkHours,
            Quantity = 8.5m,
        });

        var result = _formatter.Format(data, Config(baseWageType: "1000"));
        var fields = Lines(result.Content)[1].Split(';');

        fields[0].ShouldBe("42");
        fields[1].ShouldBe("2026-01-15");
        fields[2].ShouldBe("1000");
        fields[3].ShouldBe("8.50");
        result.RecordCount.ShouldBe(1);
        result.SkippedAbsenceCount.ShouldBe(0);
    }

    [Test]
    public void Format_UsesConfiguredDelimiter()
    {
        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.WorkHours,
            Quantity = 8m,
        });

        var result = _formatter.Format(data, Config(delimiter: "|", baseWageType: "1000"));
        var lines = Lines(result.Content);

        lines[0].ShouldBe("PersonnelNumber|Date|WageType|Quantity");
        lines[1].Split('|').Length.ShouldBe(4);
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
        Lines(result.Content).Length.ShouldBe(1);
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
        var fields = Lines(result.Content)[1].Split(';');

        result.RecordCount.ShouldBe(1);
        fields[2].ShouldBe("1500");
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
        var fields = Lines(result.Content)[1].Split(';');

        result.RecordCount.ShouldBe(1);
        result.SkippedAbsenceCount.ShouldBe(0);
        fields[2].ShouldBe("2000");
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
        Lines(result.Content).Length.ShouldBe(1);
    }

    [Test]
    public void Format_DefaultsToUtf8_WhenEncodingUnset()
    {
        var result = _formatter.Format(DataWith(), Config(encoding: string.Empty));

        Lines(result.Content, "utf-8")[0].ShouldBe("PersonnelNumber;Date;WageType;Quantity");
    }

    [Test]
    public void Format_ExposesFormatKeyContentTypeAndExtension()
    {
        _formatter.FormatKey.ShouldBe(PayrollExportConstants.FormatKeyGenericPayrollCsv);
        _formatter.ContentType.ShouldBe(PayrollExportConstants.ContentTypeCsv);
        _formatter.FileExtension.ShouldBe(PayrollExportConstants.FileExtensionCsv);
    }
}
