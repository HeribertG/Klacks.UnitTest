// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the KLARA Lohn (Switzerland) variable-salary-item transfer list: exact byte output of header and
/// data rows, fixed semicolon delimiter, dot decimal separator, UTF-8 with BOM, CRLF, aggregation per employee,
/// month, Lohnart and unit, rounding, mapped/unmapped absences, day-based entries, empty wage types, sanitising and
/// skip accounting. KLARA documents no file import for payroll entries, so the layout under test is the Klacks
/// design described in the formatter remarks, not a vendor spec.
/// </summary>

using System.Text;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Services.Exports;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class KlaraChExportFormatterTests
{
    private const string Crlf = "\r\n";

    private const string ExpectedHeader = "Personalnummer;Nachname;Vorname;Periode;Lohnart;Menge;Einheit" + Crlf;

    private static readonly byte[] Utf8Bom = [0xEF, 0xBB, 0xBF];

    private KlaraChExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new KlaraChExportFormatter();
    }

    private static PayrollExportGroupConfig Config(
        string baseWageType = "1000",
        string surchargeWageType = "",
        string absenceMappingJson = "{}",
        string delimiter = ";",
        string encoding = "utf-8")
    {
        return new PayrollExportGroupConfig
        {
            TargetSystem = PayrollExportConstants.FormatKeyKlaraCh,
            Delimiter = delimiter,
            Encoding = encoding,
            BaseWageType = baseWageType,
            SurchargeWageType = surchargeWageType,
            AbsenceMappingJson = absenceMappingJson,
        };
    }

    private static PayrollEmployee Employee(int idNumber, string lastName, string firstName, params PayrollDayEntry[] entries)
    {
        return new PayrollEmployee
        {
            ClientId = Guid.NewGuid(),
            IdNumber = idNumber,
            FullName = $"{lastName}, {firstName}",
            LastName = lastName,
            FirstName = firstName,
            Entries = entries.ToList(),
        };
    }

    private static PayrollExportData DataWith(params PayrollDayEntry[] entries)
    {
        return DataFor(Employee(42, "Muster", "Anna", entries));
    }

    private static PayrollExportData DataFor(params PayrollEmployee[] employees)
    {
        return new PayrollExportData
        {
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 31),
            Employees = employees.ToList(),
        };
    }

    private static PayrollDayEntry Hours(PayrollEntryKind kind, int day, decimal quantity, Guid? absenceId = null)
    {
        return new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, day),
            Kind = kind,
            Quantity = quantity,
            Unit = PayrollQuantityUnit.Hours,
            AbsenceId = absenceId,
        };
    }

    private static PayrollDayEntry Days(PayrollEntryKind kind, int day, decimal quantity, Guid? absenceId = null)
    {
        return new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, day),
            Kind = kind,
            Quantity = quantity,
            Unit = PayrollQuantityUnit.Days,
            AbsenceId = absenceId,
        };
    }

    private static string Mapping(Guid absenceId, string wageType)
    {
        return $"{{\"{absenceId}\":{{\"WageType\":\"{wageType}\"}}}}";
    }

    private static string Decode(byte[] content)
    {
        content.Take(Utf8Bom.Length).ToArray().ShouldBe(Utf8Bom);
        return Encoding.UTF8.GetString(content, Utf8Bom.Length, content.Length - Utf8Bom.Length);
    }

    [Test]
    public void Format_WorkHours_ProducesExactHeaderAndSevenColumnRow()
    {
        var data = DataWith(Hours(PayrollEntryKind.WorkHours, 15, 8.5m));

        var result = _formatter.Format(data, Config(baseWageType: "1000"));

        Decode(result.Content).ShouldBe(ExpectedHeader + "42;Muster;Anna;2026-01;1000;8.50;Stunden" + Crlf);
        result.RecordCount.ShouldBe(1);
        result.EmittedEntryCount.ShouldBe(1);
        result.TotalSkippedCount.ShouldBe(0);
    }

    [Test]
    public void Format_EmptyData_WritesBomAndHeaderOnly()
    {
        var result = _formatter.Format(DataFor(), Config());

        result.Content.ShouldBe(Utf8Bom.Concat(Encoding.UTF8.GetBytes(ExpectedHeader)).ToArray());
        result.RecordCount.ShouldBe(0);
        result.EmittedEntryCount.ShouldBe(0);
    }

    [Test]
    public void Format_HeaderAndRows_HaveExactlySevenColumns()
    {
        var data = DataWith(Hours(PayrollEntryKind.WorkHours, 15, 8m));

        var lines = Decode(_formatter.Format(data, Config()).Content)
            .Split(Crlf, StringSplitOptions.RemoveEmptyEntries);

        lines.Length.ShouldBe(2);
        foreach (var line in lines)
        {
            line.Split(';').Length.ShouldBe(7);
        }
    }

    [Test]
    public void Format_WritesUtf8WithBomAndEncodesUmlauts()
    {
        var data = DataFor(Employee(7, "Müller", "Jürg", Hours(PayrollEntryKind.WorkHours, 2, 1m)));

        var result = _formatter.Format(data, Config(encoding: "windows-1252"));

        result.Content.ShouldBe(Utf8Bom
            .Concat(Encoding.UTF8.GetBytes(ExpectedHeader + "7;Müller;Jürg;2026-01;1000;1.00;Stunden" + Crlf))
            .ToArray());
    }

    [Test]
    public void Format_IgnoresConfiguredDelimiter_LayoutFixesSemicolon()
    {
        var data = DataWith(Hours(PayrollEntryKind.WorkHours, 15, 4m));

        var result = _formatter.Format(data, Config(delimiter: ","));

        Decode(result.Content).ShouldBe(ExpectedHeader + "42;Muster;Anna;2026-01;1000;4.00;Stunden" + Crlf);
    }

    [Test]
    public void Format_UsesDotDecimalSeparatorWithoutThousandsSeparator()
    {
        var data = DataWith(Hours(PayrollEntryKind.WorkHours, 1, 1234.5m));

        var result = _formatter.Format(data, Config());

        Decode(result.Content).ShouldBe(ExpectedHeader + "42;Muster;Anna;2026-01;1000;1234.50;Stunden" + Crlf);
    }

    [Test]
    public void Format_SumsExactlyThenRoundsOnceAwayFromZero()
    {
        var data = DataWith(
            Hours(PayrollEntryKind.WorkHours, 1, 0.333m),
            Hours(PayrollEntryKind.WorkHours, 2, 0.333m),
            Hours(PayrollEntryKind.WorkHours, 3, 0.339m));

        var result = _formatter.Format(data, Config());

        Decode(result.Content).ShouldBe(ExpectedHeader + "42;Muster;Anna;2026-01;1000;1.01;Stunden" + Crlf);
        result.EmittedEntryCount.ShouldBe(3);
        result.RecordCount.ShouldBe(1);
    }

    [Test]
    public void Format_AggregatesPerEmployeeMonthWageTypeAndUnit_InFirstSeenOrder()
    {
        var absenceId = Guid.NewGuid();
        var data = DataFor(
            Employee(
                42,
                "Muster",
                "Anna",
                Hours(PayrollEntryKind.WorkHours, 5, 8m),
                Hours(PayrollEntryKind.Surcharge, 5, 2m),
                Hours(PayrollEntryKind.WorkHours, 6, 7.25m),
                Hours(PayrollEntryKind.Absence, 7, 8m, absenceId),
                Hours(PayrollEntryKind.Surcharge, 8, 1.5m)),
            Employee(43, "Keller", "Beat", Hours(PayrollEntryKind.WorkHours, 5, 6m)));

        var result = _formatter.Format(data, Config(surchargeWageType: "1100", absenceMappingJson: Mapping(absenceId, "1300")));

        Decode(result.Content).ShouldBe(
            ExpectedHeader
            + "42;Muster;Anna;2026-01;1000;15.25;Stunden" + Crlf
            + "42;Muster;Anna;2026-01;1100;3.50;Stunden" + Crlf
            + "42;Muster;Anna;2026-01;1300;8.00;Stunden" + Crlf
            + "43;Keller;Beat;2026-01;1000;6.00;Stunden" + Crlf);
        result.RecordCount.ShouldBe(4);
        result.EmittedEntryCount.ShouldBe(6);
        result.TotalSkippedCount.ShouldBe(0);
    }

    [Test]
    public void Format_EntriesInDifferentMonths_AreWrittenAsSeparatePeriods()
    {
        var data = DataWith(
            Hours(PayrollEntryKind.WorkHours, 31, 8m),
            new PayrollDayEntry
            {
                Date = new DateOnly(2026, 2, 1),
                Kind = PayrollEntryKind.WorkHours,
                Quantity = 4m,
            });

        var result = _formatter.Format(data, Config());

        Decode(result.Content).ShouldBe(
            ExpectedHeader
            + "42;Muster;Anna;2026-01;1000;8.00;Stunden" + Crlf
            + "42;Muster;Anna;2026-02;1000;4.00;Stunden" + Crlf);
        result.RecordCount.ShouldBe(2);
    }

    [Test]
    public void Format_MappedAbsence_UsesMappedWageType()
    {
        var absenceId = Guid.NewGuid();
        var data = DataWith(Hours(PayrollEntryKind.Absence, 20, 8.4m, absenceId));

        var result = _formatter.Format(data, Config(absenceMappingJson: Mapping(absenceId, "1350")));

        Decode(result.Content).ShouldBe(ExpectedHeader + "42;Muster;Anna;2026-01;1350;8.40;Stunden" + Crlf);
        result.SkippedAbsenceCount.ShouldBe(0);
        result.EmittedEntryCount.ShouldBe(1);
    }

    [Test]
    public void Format_UnmappedEmptyMappedOrIdlessAbsence_IsSkippedAndCounted()
    {
        var emptyMappedId = Guid.NewGuid();
        var data = DataWith(
            Hours(PayrollEntryKind.Absence, 20, 8m, Guid.NewGuid()),
            Hours(PayrollEntryKind.Absence, 21, 8m, emptyMappedId),
            Hours(PayrollEntryKind.Absence, 22, 8m));

        var result = _formatter.Format(data, Config(absenceMappingJson: Mapping(emptyMappedId, string.Empty)));

        Decode(result.Content).ShouldBe(ExpectedHeader);
        result.RecordCount.ShouldBe(0);
        result.EmittedEntryCount.ShouldBe(0);
        result.SkippedAbsenceCount.ShouldBe(3);
    }

    [Test]
    public void Format_InvalidAbsenceMapping_IsReportedAndAbsenceCounted()
    {
        var data = DataWith(Hours(PayrollEntryKind.Absence, 20, 8m, Guid.NewGuid()));

        var result = _formatter.Format(data, Config(absenceMappingJson: "{ not json"));

        result.AbsenceMappingInvalid.ShouldBeTrue();
        result.SkippedAbsenceCount.ShouldBe(1);
        Decode(result.Content).ShouldBe(ExpectedHeader);
    }

    [Test]
    public void Format_DayBasedMappedAbsence_IsWrittenAsDaysInOwnRow()
    {
        var absenceId = Guid.NewGuid();
        var data = DataWith(
            Hours(PayrollEntryKind.Absence, 10, 8m, absenceId),
            Days(PayrollEntryKind.Absence, 11, 1m, absenceId),
            Days(PayrollEntryKind.Absence, 12, 1m, absenceId));

        var result = _formatter.Format(data, Config(absenceMappingJson: Mapping(absenceId, "1400")));

        Decode(result.Content).ShouldBe(
            ExpectedHeader
            + "42;Muster;Anna;2026-01;1400;8.00;Stunden" + Crlf
            + "42;Muster;Anna;2026-01;1400;2.00;Tage" + Crlf);
        result.RecordCount.ShouldBe(2);
        result.EmittedEntryCount.ShouldBe(3);
        result.TotalSkippedCount.ShouldBe(0);
    }

    [Test]
    public void Format_DayBasedWorkHoursAndSurcharge_AreCountedAsUnsupportedUnit()
    {
        var data = DataWith(
            Days(PayrollEntryKind.WorkHours, 20, 1m),
            Days(PayrollEntryKind.Surcharge, 21, 1m));

        var result = _formatter.Format(data, Config(surchargeWageType: "1100"));

        Decode(result.Content).ShouldBe(ExpectedHeader);
        result.SkippedUnsupportedUnitCount.ShouldBe(2);
        result.EmittedEntryCount.ShouldBe(0);
    }

    [Test]
    public void Format_WorkHoursWithoutBaseWageType_IsSkippedAndCounted()
    {
        var data = DataWith(Hours(PayrollEntryKind.WorkHours, 15, 8m));

        var result = _formatter.Format(data, Config(baseWageType: string.Empty));

        Decode(result.Content).ShouldBe(ExpectedHeader);
        result.RecordCount.ShouldBe(0);
        result.SkippedUnmappedBaseWageCount.ShouldBe(1);
    }

    [Test]
    public void Format_WhitespaceOnlyBaseWageType_IsTreatedAsEmpty()
    {
        var data = DataWith(Hours(PayrollEntryKind.WorkHours, 15, 8m));

        var result = _formatter.Format(data, Config(baseWageType: "  "));

        Decode(result.Content).ShouldBe(ExpectedHeader);
        result.SkippedUnmappedBaseWageCount.ShouldBe(1);
    }

    [Test]
    public void Format_Surcharge_IsSkippedWhenNoSurchargeWageTypeConfigured()
    {
        var data = DataWith(Hours(PayrollEntryKind.Surcharge, 15, 2m));

        var result = _formatter.Format(data, Config(surchargeWageType: string.Empty));

        Decode(result.Content).ShouldBe(ExpectedHeader);
        result.RecordCount.ShouldBe(0);
        result.SkippedUnmappedSurchargeCount.ShouldBe(1);
    }

    [Test]
    public void Format_Surcharge_IsEmittedWithSurchargeWageTypeWhenConfigured()
    {
        var data = DataWith(Hours(PayrollEntryKind.Surcharge, 15, 2m));

        var result = _formatter.Format(data, Config(surchargeWageType: "1100"));

        Decode(result.Content).ShouldBe(ExpectedHeader + "42;Muster;Anna;2026-01;1100;2.00;Stunden" + Crlf);
        result.RecordCount.ShouldBe(1);
    }

    [Test]
    public void Format_UnknownEntryKind_IsCountedAsUnsupportedKind()
    {
        var data = DataWith(Hours((PayrollEntryKind)99, 15, 2m));

        var result = _formatter.Format(data, Config());

        Decode(result.Content).ShouldBe(ExpectedHeader);
        result.SkippedUnsupportedKindCount.ShouldBe(1);
    }

    [Test]
    public void Format_EveryEntryIsEmittedOrCountedExactlyOnce()
    {
        var absenceId = Guid.NewGuid();
        var data = DataWith(
            Hours(PayrollEntryKind.WorkHours, 1, 8m),
            Days(PayrollEntryKind.WorkHours, 2, 1m),
            Hours(PayrollEntryKind.Surcharge, 3, 2m),
            Hours(PayrollEntryKind.Absence, 4, 8m, absenceId),
            Days(PayrollEntryKind.Absence, 5, 1m, absenceId),
            Hours(PayrollEntryKind.Absence, 6, 8m, Guid.NewGuid()),
            Hours((PayrollEntryKind)99, 7, 1m));

        var result = _formatter.Format(data, Config(surchargeWageType: string.Empty, absenceMappingJson: Mapping(absenceId, "1300")));

        result.EmittedEntryCount.ShouldBe(3);
        result.SkippedUnsupportedUnitCount.ShouldBe(1);
        result.SkippedUnmappedSurchargeCount.ShouldBe(1);
        result.SkippedAbsenceCount.ShouldBe(1);
        result.SkippedUnsupportedKindCount.ShouldBe(1);
        (result.EmittedEntryCount + result.TotalSkippedCount).ShouldBe(7);
    }

    [Test]
    public void Format_StripsDelimiterAndLineBreaksFromNames()
    {
        var data = DataFor(Employee(9, "Mus;\r\nter", "An\nna", Hours(PayrollEntryKind.WorkHours, 1, 2m)));

        var result = _formatter.Format(data, Config());

        Decode(result.Content).ShouldBe(ExpectedHeader + "9;Muster;Anna;2026-01;1000;2.00;Stunden" + Crlf);
    }

    [Test]
    public void Format_NeutralizesFormulaTriggerInNames()
    {
        var data = DataFor(Employee(9, "=HYPERLINK(\"x\")", "Anna", Hours(PayrollEntryKind.WorkHours, 1, 2m)));

        var result = _formatter.Format(data, Config());

        Decode(result.Content).ShouldBe(ExpectedHeader + "9;'=HYPERLINK(\"x\");Anna;2026-01;1000;2.00;Stunden" + Crlf);
    }

    [Test]
    public void Format_ExposesFormatKeyContentTypeAndExtension()
    {
        _formatter.FormatKey.ShouldBe(PayrollExportConstants.FormatKeyKlaraCh);
        _formatter.FormatKey.ShouldBe("klara-ch");
        _formatter.ContentType.ShouldBe(PayrollExportConstants.ContentTypeCsv);
        _formatter.FileExtension.ShouldBe(PayrollExportConstants.FileExtensionCsv);
    }
}
