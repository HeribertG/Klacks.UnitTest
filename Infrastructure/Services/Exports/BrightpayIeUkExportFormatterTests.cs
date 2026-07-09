// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the BrightPay IE/UK "Import Hourly Payments" CSV formatter: header row, works-number
/// join key, normal-hours vs time-and-a-half column placement, surcharge gating and unmapped-absence
/// skipping.
/// </summary>

using System.Text;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Services.Exports;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class BrightpayIeUkExportFormatterTests
{
    private BrightpayIeUkExportFormatter _formatter = null!;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [SetUp]
    public void Setup()
    {
        _formatter = new BrightpayIeUkExportFormatter();
    }

    private static PayrollExportGroupConfig Config(
        string delimiter = ",",
        string encoding = "utf-8",
        string baseWageType = "Basic Pay",
        string surchargeWageType = "",
        string absenceMappingJson = "{}")
    {
        return new PayrollExportGroupConfig
        {
            GroupId = Guid.NewGuid(),
            TargetSystem = PayrollExportConstants.FormatKeyBrightpayIeUk,
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
                    FullName = "Byrne, Aoife",
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

        lines[0].ShouldBe("Works number,Description,Number of normal hours,Number of time and a half hours");
    }

    [Test]
    public void Format_WorkHours_ProducesRowWithWorksNumberDescriptionAndNormalHours()
    {
        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.WorkHours,
            Quantity = 8.5m,
        });

        var result = _formatter.Format(data, Config(baseWageType: "Basic Pay"));
        var fields = Lines(result.Content)[1].Split(',');

        fields[0].ShouldBe("42");
        fields[1].ShouldBe("Basic Pay");
        fields[2].ShouldBe("8.50");
        fields[3].ShouldBe(string.Empty);
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

        var result = _formatter.Format(data, Config(delimiter: "|", baseWageType: "Basic Pay"));
        var lines = Lines(result.Content);

        lines[0].ShouldBe("Works number|Description|Number of normal hours|Number of time and a half hours");
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
    public void Format_Surcharge_IsEmittedInTimeAndAHalfColumnWhenConfigured()
    {
        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.Surcharge,
            Quantity = 2m,
        });

        var result = _formatter.Format(data, Config(surchargeWageType: "Overtime Premium"));
        var fields = Lines(result.Content)[1].Split(',');

        result.RecordCount.ShouldBe(1);
        fields[1].ShouldBe("Overtime Premium");
        fields[2].ShouldBe(string.Empty);
        fields[3].ShouldBe("2.00");
    }

    [Test]
    public void Format_MappedAbsence_UsesConfiguredDescriptionInNormalHoursColumn()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":{{\"ausfallschluessel\":\"\",\"wageType\":\"Sick Pay\"}}}}";

        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 20),
            Kind = PayrollEntryKind.Absence,
            Quantity = 8m,
            AbsenceId = absenceId,
        });

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));
        var fields = Lines(result.Content)[1].Split(',');

        result.RecordCount.ShouldBe(1);
        result.SkippedAbsenceCount.ShouldBe(0);
        fields[1].ShouldBe("Sick Pay");
        fields[2].ShouldBe("8.00");
        fields[3].ShouldBe(string.Empty);
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

        Lines(result.Content, "utf-8")[0].ShouldBe("Works number,Description,Number of normal hours,Number of time and a half hours");
    }

    [Test]
    public void Format_DefaultsToCommaDelimiter_WhenDelimiterUnset()
    {
        var result = _formatter.Format(DataWith(), Config(delimiter: string.Empty));

        Lines(result.Content)[0].ShouldBe("Works number,Description,Number of normal hours,Number of time and a half hours");
    }

    [Test]
    public void Format_ExposesFormatKeyContentTypeAndExtension()
    {
        _formatter.FormatKey.ShouldBe(PayrollExportConstants.FormatKeyBrightpayIeUk);
        _formatter.ContentType.ShouldBe(PayrollExportConstants.ContentTypeCsv);
        _formatter.FileExtension.ShouldBe(PayrollExportConstants.FileExtensionCsv);
    }
}
