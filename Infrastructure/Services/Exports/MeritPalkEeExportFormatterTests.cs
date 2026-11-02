// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the Merit Palk (Estonia) "Tasude import" formatter against the vendor spec
/// (merit-palga-tasude-import-formaadi-kirjeldus.pdf, May 2023): exact byte output of header and data rows,
/// fixed semicolon delimiter, comma decimal separator, UTF-8 without BOM, CRLF, per-employee/per-code
/// aggregation, rounding, wage-code mapping, deduction sign pass-through and skip accounting.
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
    private const string Crlf = "\r\n";

    private const string ExpectedHeader =
        "töötaja isikukood või lepingu impordi kood;vaba sisuga veerg;vaba sisuga veerg;"
        + "tasuliigi või kinnipidamise impordi kood;määr või summa;kogus;kohustuslik tühi veerg;"
        + "osakonna kood;kulukoha kood;projekti kood;normtundide arv;normpäevade arv" + Crlf;

    private MeritPalkEeExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new MeritPalkEeExportFormatter();
    }

    private static PayrollExportGroupConfig Config(
        string baseWageType = "100",
        string surchargeWageType = "",
        string absenceMappingJson = "{}",
        string delimiter = ";",
        string encoding = "utf-8")
    {
        return new PayrollExportGroupConfig
        {
            TargetSystem = PayrollExportConstants.FormatKeyMeritPalkEe,
            Delimiter = delimiter,
            Encoding = encoding,
            BaseWageType = baseWageType,
            SurchargeWageType = surchargeWageType,
            AbsenceMappingJson = absenceMappingJson,
        };
    }

    private static PayrollEmployee Employee(int idNumber, string fullName, params PayrollDayEntry[] entries)
    {
        return new PayrollEmployee
        {
            ClientId = Guid.NewGuid(),
            IdNumber = idNumber,
            FullName = fullName,
            Entries = entries.ToList(),
        };
    }

    private static PayrollExportData DataWith(params PayrollDayEntry[] entries)
    {
        return DataFor(Employee(42, "Tamm, Mari", entries));
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
            AbsenceId = absenceId,
        };
    }

    private static string Decode(byte[] content)
    {
        return Encoding.UTF8.GetString(content);
    }

    [Test]
    public void Format_WorkHours_ProducesExactHeaderAndTwelveColumnRow()
    {
        var data = DataWith(Hours(PayrollEntryKind.WorkHours, 15, 8.5m));

        var result = _formatter.Format(data, Config(baseWageType: "100"));

        Decode(result.Content).ShouldBe(ExpectedHeader + "42;Tamm, Mari;;100;;8,50;;;;;;" + Crlf);
        result.RecordCount.ShouldBe(1);
        result.EmittedEntryCount.ShouldBe(1);
        result.TotalSkippedCount.ShouldBe(0);
    }

    [Test]
    public void Format_HeaderAndRows_HaveExactlyTwelveColumns()
    {
        var data = DataWith(Hours(PayrollEntryKind.WorkHours, 15, 8m));

        var lines = Decode(_formatter.Format(data, Config()).Content)
            .Split(Crlf, StringSplitOptions.RemoveEmptyEntries);

        lines.Length.ShouldBe(2);
        foreach (var line in lines)
        {
            line.Split(';').Length.ShouldBe(PayrollExportConstants.MeritPalkFieldCount);
        }
    }

    [Test]
    public void Format_WritesUtf8WithoutBomAndEncodesEstonianCharacters()
    {
        var data = DataFor(Employee(7, "Õunapuu, Jüri", Hours(PayrollEntryKind.WorkHours, 2, 1m)));

        var result = _formatter.Format(data, Config(encoding: "windows-1252"));

        result.Content[0].ShouldNotBe((byte)0xEF);
        result.Content.ShouldBe(Encoding.UTF8.GetBytes(ExpectedHeader + "7;Õunapuu, Jüri;;100;;1,00;;;;;;" + Crlf));
    }

    [Test]
    public void Format_IgnoresConfiguredDelimiter_SpecFixesSemicolon()
    {
        var data = DataWith(Hours(PayrollEntryKind.WorkHours, 15, 4m));

        var result = _formatter.Format(data, Config(delimiter: ","));

        Decode(result.Content).ShouldBe(ExpectedHeader + "42;Tamm, Mari;;100;;4,00;;;;;;" + Crlf);
    }

    [Test]
    public void Format_AggregatesDaysPerEmployeeAndImportCode_InFirstSeenOrder()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":\"200\"}}";
        var data = DataFor(
            Employee(
                42,
                "Tamm, Mari",
                Hours(PayrollEntryKind.WorkHours, 5, 8m),
                Hours(PayrollEntryKind.Surcharge, 5, 2m),
                Hours(PayrollEntryKind.WorkHours, 6, 7.25m),
                Hours(PayrollEntryKind.Absence, 7, 8m, absenceId),
                Hours(PayrollEntryKind.Surcharge, 8, 1.5m)),
            Employee(43, "Kask, Jaan", Hours(PayrollEntryKind.WorkHours, 5, 6m)));

        var result = _formatter.Format(data, Config(surchargeWageType: "150", absenceMappingJson: mapping));

        Decode(result.Content).ShouldBe(
            ExpectedHeader
            + "42;Tamm, Mari;;100;;15,25;;;;;;" + Crlf
            + "42;Tamm, Mari;;150;;3,50;;;;;;" + Crlf
            + "42;Tamm, Mari;;200;;8,00;;;;;;" + Crlf
            + "43;Kask, Jaan;;100;;6,00;;;;;;" + Crlf);
        result.RecordCount.ShouldBe(4);
        result.EmittedEntryCount.ShouldBe(6);
        result.TotalSkippedCount.ShouldBe(0);
    }

    [Test]
    public void Format_SumsExactlyThenRoundsOnceAwayFromZero()
    {
        var data = DataWith(
            Hours(PayrollEntryKind.WorkHours, 1, 0.333m),
            Hours(PayrollEntryKind.WorkHours, 2, 0.333m),
            Hours(PayrollEntryKind.WorkHours, 3, 0.339m));

        var result = _formatter.Format(data, Config());

        Decode(result.Content).ShouldBe(ExpectedHeader + "42;Tamm, Mari;;100;;1,01;;;;;;" + Crlf);
    }

    [Test]
    public void Format_LargeQuantity_HasNoThousandsSeparator()
    {
        var data = DataWith(Hours(PayrollEntryKind.WorkHours, 1, 1234.5m));

        var result = _formatter.Format(data, Config());

        Decode(result.Content).ShouldBe(ExpectedHeader + "42;Tamm, Mari;;100;;1234,50;;;;;;" + Crlf);
    }

    [Test]
    public void Format_WorkHoursWithoutBaseWageType_IsSkippedBecauseColumnDIsMandatory()
    {
        var data = DataWith(Hours(PayrollEntryKind.WorkHours, 15, 8m));

        var result = _formatter.Format(data, Config(baseWageType: string.Empty));

        Decode(result.Content).ShouldBe(ExpectedHeader);
        result.RecordCount.ShouldBe(0);
        result.SkippedUnmappedBaseWageCount.ShouldBe(1);
    }

    [Test]
    public void Format_Surcharge_IsOmittedWhenNoSurchargeWageTypeConfigured()
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

        var result = _formatter.Format(data, Config(surchargeWageType: "150"));

        Decode(result.Content).ShouldBe(ExpectedHeader + "42;Tamm, Mari;;150;;2,00;;;;;;" + Crlf);
        result.RecordCount.ShouldBe(1);
    }

    [Test]
    public void Format_MappedDeduction_KeepsLeadingMinusOnImportCode()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":\"-105\"}}";
        var data = DataWith(Hours(PayrollEntryKind.Absence, 20, 8m, absenceId));

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));

        Decode(result.Content).ShouldBe(ExpectedHeader + "42;Tamm, Mari;;-105;;8,00;;;;;;" + Crlf);
        result.SkippedAbsenceCount.ShouldBe(0);
    }

    [Test]
    public void Format_UnmappedOrEmptyMappedAbsence_IsSkippedAndCounted()
    {
        var emptyMappedId = Guid.NewGuid();
        var mapping = $"{{\"{emptyMappedId}\":\"\"}}";
        var data = DataWith(
            Hours(PayrollEntryKind.Absence, 20, 8m, Guid.NewGuid()),
            Hours(PayrollEntryKind.Absence, 21, 8m, emptyMappedId));

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));

        Decode(result.Content).ShouldBe(ExpectedHeader);
        result.RecordCount.ShouldBe(0);
        result.SkippedAbsenceCount.ShouldBe(2);
    }

    [Test]
    public void Format_DayBasedEntry_IsNotWrittenIntoHoursColumn()
    {
        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 20),
            Kind = PayrollEntryKind.WorkHours,
            Quantity = 1m,
            Unit = PayrollQuantityUnit.Days,
        });

        var result = _formatter.Format(data, Config());

        Decode(result.Content).ShouldBe(ExpectedHeader);
        result.SkippedUnsupportedUnitCount.ShouldBe(1);
    }

    [Test]
    public void Format_StripsDelimiterAndLineBreaksFromFreeText()
    {
        var data = DataFor(Employee(9, "Tamm;\r\nMari", Hours(PayrollEntryKind.WorkHours, 1, 2m)));

        var result = _formatter.Format(data, Config());

        Decode(result.Content).ShouldBe(ExpectedHeader + "9;TammMari;;100;;2,00;;;;;;" + Crlf);
    }

    [Test]
    public void Format_ExposesFormatKeyContentTypeAndExtension()
    {
        _formatter.FormatKey.ShouldBe(PayrollExportConstants.FormatKeyMeritPalkEe);
        _formatter.ContentType.ShouldBe(PayrollExportConstants.ContentTypeCsv);
        _formatter.FileExtension.ShouldBe(PayrollExportConstants.FileExtensionCsv);
    }
}
