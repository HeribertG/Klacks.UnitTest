// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the Merit Palk (Estonia) "Tasude import" formatter: field layout, delimiter,
/// decimal separator, no-header output, wage-code mapping, surcharge gating and unmapped-absence
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
public class MeritPalkEeExportFormatterTests
{
    private MeritPalkEeExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new MeritPalkEeExportFormatter();
    }

    private static PayrollExportGroupConfig Config(
        string baseWageType = "100",
        string surchargeWageType = "",
        string absenceMappingJson = "{}")
    {
        return new PayrollExportGroupConfig
        {
            GroupId = Guid.NewGuid(),
            TargetSystem = PayrollExportConstants.FormatKeyMeritPalkEe,
            Delimiter = PayrollExportConstants.DefaultDelimiter,
            Encoding = "utf-8",
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
                    FullName = "Tamm, Mari",
                    Entries = entries.ToList(),
                },
            ],
        };
    }

    private static string Decode(byte[] content)
    {
        return Encoding.UTF8.GetString(content);
    }

    [Test]
    public void Format_WorkHours_ProducesTwelveFieldLineWithBaseWageTypeInQuantityColumn()
    {
        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.WorkHours,
            Quantity = 8.5m,
        });

        var result = _formatter.Format(data, Config(baseWageType: "100"));
        var text = Decode(result.Content);

        text.ShouldEndWith(PayrollExportConstants.LineEnding);
        var line = text.Replace(PayrollExportConstants.LineEnding, string.Empty);
        var fields = line.Split(PayrollExportConstants.DefaultDelimiter);

        fields.Length.ShouldBe(PayrollExportConstants.MeritPalkFieldCount);
        fields[0].ShouldBe("42");
        fields[1].ShouldBe("Tamm, Mari".Replace(PayrollExportConstants.DefaultDelimiter, string.Empty));
        fields[2].ShouldBe(string.Empty);
        fields[3].ShouldBe("100");
        fields[4].ShouldBe(string.Empty);
        fields[5].ShouldBe("8,50");
        fields[6].ShouldBe(string.Empty);
        result.RecordCount.ShouldBe(1);
        result.SkippedAbsenceCount.ShouldBe(0);
    }

    [Test]
    public void Format_UsesSemicolonDelimiterAndNoHeaderRow()
    {
        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.WorkHours,
            Quantity = 4m,
        });

        var result = _formatter.Format(data, Config());
        var text = Decode(result.Content);

        text.ShouldContain(PayrollExportConstants.DefaultDelimiter);
        text.ShouldNotContain("Personalnumber");
        text.ShouldNotContain("veerg");
        var lineCount = text.Split(PayrollExportConstants.LineEnding, StringSplitOptions.RemoveEmptyEntries).Length;
        lineCount.ShouldBe(1);
    }

    [Test]
    public void Format_MandatoryColumnGIsAlwaysEmpty()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":\"200\"}}";

        var data = DataWith(
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 10), Kind = PayrollEntryKind.WorkHours, Quantity = 8m },
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 11), Kind = PayrollEntryKind.Surcharge, Quantity = 2m },
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 12), Kind = PayrollEntryKind.Absence, Quantity = 8m, AbsenceId = absenceId });

        var result = _formatter.Format(data, Config(surchargeWageType: "150", absenceMappingJson: mapping));
        var lines = Decode(result.Content)
            .Split(PayrollExportConstants.LineEnding, StringSplitOptions.RemoveEmptyEntries);

        foreach (var line in lines)
        {
            var fields = line.Split(PayrollExportConstants.DefaultDelimiter);
            fields[6].ShouldBe(string.Empty);
        }

        result.RecordCount.ShouldBe(3);
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
        Decode(result.Content).ShouldBeEmpty();
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

        var result = _formatter.Format(data, Config(surchargeWageType: "150"));
        var fields = Decode(result.Content)
            .Replace(PayrollExportConstants.LineEnding, string.Empty)
            .Split(PayrollExportConstants.DefaultDelimiter);

        result.RecordCount.ShouldBe(1);
        fields[3].ShouldBe("150");
        fields[5].ShouldBe("2,00");
    }

    [Test]
    public void Format_MappedAbsence_UsesConfiguredImportCodeInWageColumn()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":\"-105\"}}";

        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 20),
            Kind = PayrollEntryKind.Absence,
            Quantity = 8m,
            AbsenceId = absenceId,
        });

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));
        var fields = Decode(result.Content)
            .Replace(PayrollExportConstants.LineEnding, string.Empty)
            .Split(PayrollExportConstants.DefaultDelimiter);

        result.RecordCount.ShouldBe(1);
        result.SkippedAbsenceCount.ShouldBe(0);
        fields[3].ShouldBe("-105");
        fields[5].ShouldBe("8,00");
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
        Decode(result.Content).ShouldBeEmpty();
    }

    [Test]
    public void Format_ExposesFormatKeyContentTypeAndExtension()
    {
        _formatter.FormatKey.ShouldBe(PayrollExportConstants.FormatKeyMeritPalkEe);
        _formatter.ContentType.ShouldBe(PayrollExportConstants.ContentTypeCsv);
        _formatter.FileExtension.ShouldBe(PayrollExportConstants.FileExtensionCsv);
    }
}
