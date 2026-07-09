// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the DATEV Lohn &amp; Gehalt Bewegungsdaten formatter: field layout, delimiter, line ending,
/// encoding, wage-type mapping, surcharge gating and unmapped-absence skipping.
/// </summary>

using System.Text;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Services.Exports;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class DatevLugBewegungsdatenFormatterTests
{
    private DatevLugBewegungsdatenFormatter _formatter = null!;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [SetUp]
    public void Setup()
    {
        _formatter = new DatevLugBewegungsdatenFormatter();
    }

    private static PayrollExportGroupConfig Config(
        string baseWageType = "1000",
        string surchargeWageType = "",
        string absenceMappingJson = "{}")
    {
        return new PayrollExportGroupConfig
        {
            GroupId = Guid.NewGuid(),
            TargetSystem = PayrollExportConstants.FormatKeyDatevLug,
            Delimiter = PayrollExportConstants.DefaultDelimiter,
            Encoding = PayrollExportConstants.DefaultEncoding,
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

    private static string Decode(byte[] content)
    {
        return Encoding.GetEncoding(PayrollExportConstants.Windows1252CodePage).GetString(content);
    }

    [Test]
    public void Format_WorkHours_ProducesElevenFieldLineWithBaseWageTypeAndCrlf()
    {
        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.WorkHours,
            Quantity = 8.5m,
        });

        var result = _formatter.Format(data, Config(baseWageType: "1000"));
        var text = Decode(result.Content);

        text.ShouldEndWith(PayrollExportConstants.LineEnding);
        var line = text.Replace(PayrollExportConstants.LineEnding, string.Empty);
        var fields = line.Split(PayrollExportConstants.DefaultDelimiter);

        fields.Length.ShouldBe(PayrollExportConstants.DatevLugFieldCount);
        fields[0].ShouldBe("42");
        fields[1].ShouldBe("15012026");
        fields[2].ShouldBe(string.Empty);
        fields[3].ShouldBe("1000");
        fields[4].ShouldBe("8,50");
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

        var result = _formatter.Format(data, Config(surchargeWageType: "1500"));
        var fields = Decode(result.Content)
            .Replace(PayrollExportConstants.LineEnding, string.Empty)
            .Split(PayrollExportConstants.DefaultDelimiter);

        result.RecordCount.ShouldBe(1);
        fields[3].ShouldBe("1500");
        fields[4].ShouldBe("2,00");
    }

    [Test]
    public void Format_MappedAbsence_UsesAusfallschluesselAndWageType()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":{{\"ausfallschluessel\":\"01\",\"wageType\":\"2000\"}}}}";

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
        fields[2].ShouldBe("01");
        fields[3].ShouldBe("2000");
        fields[4].ShouldBe("8,00");
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
        _formatter.FormatKey.ShouldBe(PayrollExportConstants.FormatKeyDatevLug);
        _formatter.ContentType.ShouldBe(PayrollExportConstants.ContentTypeCsv);
        _formatter.FileExtension.ShouldBe(PayrollExportConstants.FileExtensionCsv);
    }
}
