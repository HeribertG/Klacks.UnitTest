// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the BrightPay IE/UK "Import Hourly Payments" CSV formatter: "Employee works number" plus five
/// "Payment n - Description" / "Payment n - Number of normal hours" blocks, one row per employee with per-description
/// period totals, continuation row after five descriptions, fixed comma delimiter, surcharges as normal hours under
/// SurchargeWageType, and the skip counters for unmapped wage types, unmapped absences and day-based entries.
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
    private const string Header =
        "Employee works number," +
        "Payment 1 - Description,Payment 1 - Number of normal hours," +
        "Payment 2 - Description,Payment 2 - Number of normal hours," +
        "Payment 3 - Description,Payment 3 - Number of normal hours," +
        "Payment 4 - Description,Payment 4 - Number of normal hours," +
        "Payment 5 - Description,Payment 5 - Number of normal hours";

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
            TargetSystem = PayrollExportConstants.FormatKeyBrightpayIeUk,
            Delimiter = delimiter,
            Encoding = encoding,
            BaseWageType = baseWageType,
            SurchargeWageType = surchargeWageType,
            AbsenceMappingJson = absenceMappingJson,
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

    private static PayrollExportData DataWith(params PayrollDayEntry[] entries)
    {
        return new PayrollExportData
        {
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

    private static string Text(byte[] content, string encoding = "utf-8")
    {
        return Encoding.GetEncoding(encoding).GetString(content);
    }

    private static string Csv(params string[] lines)
    {
        return string.Concat(lines.Select(line => line + PayrollExportConstants.LineEnding));
    }

    [Test]
    public void Format_NoEntries_WritesOnlyTheHeaderRow()
    {
        var result = _formatter.Format(DataWith(), Config());

        Text(result.Content).ShouldBe(Csv(Header));
        result.RecordCount.ShouldBe(0);
        result.EmittedEntryCount.ShouldBe(0);
    }

    [Test]
    public void Format_WorkHours_ProducesOneRowWithWorksNumberDescriptionAndNormalHours()
    {
        var data = DataWith(Hours(PayrollEntryKind.WorkHours, 15, 8.5m));

        var result = _formatter.Format(data, Config(baseWageType: "Basic Pay"));

        Text(result.Content).ShouldBe(Csv(Header, "42,Basic Pay,8.50,,,,,,,,"));
        result.RecordCount.ShouldBe(1);
        result.EmittedEntryCount.ShouldBe(1);
        result.TotalSkippedCount.ShouldBe(0);
    }

    [Test]
    public void Format_SeveralDays_AreSummedPerDescription_InFirstSeenOrder()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":{{\"ausfallschluessel\":\"\",\"wageType\":\"Sick Pay\"}}}}";
        var data = DataWith(
            Hours(PayrollEntryKind.WorkHours, 15, 8.5m),
            Hours(PayrollEntryKind.Surcharge, 15, 1.25m),
            Hours(PayrollEntryKind.WorkHours, 16, 7.75m),
            Hours(PayrollEntryKind.Absence, 20, 8m, absenceId),
            Hours(PayrollEntryKind.Surcharge, 21, 0.5m),
            Hours(PayrollEntryKind.Absence, 22, 4m, absenceId));

        var result = _formatter.Format(data, Config(surchargeWageType: "Premium", absenceMappingJson: mapping));

        Text(result.Content).ShouldBe(Csv(Header, "42,Basic Pay,16.25,Premium,1.75,Sick Pay,12.00,,,,"));
        result.RecordCount.ShouldBe(1);
        result.EmittedEntryCount.ShouldBe(6);
        result.TotalSkippedCount.ShouldBe(0);
    }

    [Test]
    public void Format_MoreThanFiveDescriptions_ContinuesOnASecondRowWithTheSameWorksNumber()
    {
        var ids = Enumerable.Range(0, 5).Select(_ => Guid.NewGuid()).ToArray();
        var mapping = "{" + string.Join(",", ids.Select((id, i) => $"\"{id}\":{{\"wageType\":\"A{i + 1}\"}}")) + "}";
        var entries = new List<PayrollDayEntry> { Hours(PayrollEntryKind.WorkHours, 2, 8m) };
        entries.AddRange(ids.Select((id, i) => Hours(PayrollEntryKind.Absence, i + 3, i + 1, id)));

        var result = _formatter.Format(DataWith(entries.ToArray()), Config(absenceMappingJson: mapping));

        Text(result.Content).ShouldBe(Csv(
            Header,
            "42,Basic Pay,8.00,A1,1.00,A2,2.00,A3,3.00,A4,4.00",
            "42,A5,5.00,,,,,,,,"));
        result.RecordCount.ShouldBe(2);
        result.EmittedEntryCount.ShouldBe(6);
    }

    [Test]
    public void Format_OneRowPerEmployee_InEmployeeOrder()
    {
        var data = new PayrollExportData
        {
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 31),
            Employees =
            [
                new PayrollEmployee { ClientId = Guid.NewGuid(), IdNumber = 42, Entries = [Hours(PayrollEntryKind.WorkHours, 15, 8m)] },
                new PayrollEmployee { ClientId = Guid.NewGuid(), IdNumber = 7, Entries = [] },
                new PayrollEmployee { ClientId = Guid.NewGuid(), IdNumber = 77, Entries = [Hours(PayrollEntryKind.WorkHours, 16, 6m)] },
            ],
        };

        var result = _formatter.Format(data, Config());

        Text(result.Content).ShouldBe(Csv(Header, "42,Basic Pay,8.00,,,,,,,,", "77,Basic Pay,6.00,,,,,,,,"));
        result.RecordCount.ShouldBe(2);
    }

    [Test]
    public void Format_IgnoresConfiguredDelimiter_AndAlwaysWritesComma()
    {
        var data = DataWith(Hours(PayrollEntryKind.WorkHours, 15, 8m));

        var result = _formatter.Format(data, Config(delimiter: ";"));

        Text(result.Content).ShouldBe(Csv(Header, "42,Basic Pay,8.00,,,,,,,,"));
    }

    [Test]
    public void Format_DescriptionWithCommaQuoteOrFormulaTrigger_IsSanitised()
    {
        var data = DataWith(Hours(PayrollEntryKind.WorkHours, 15, 8m));

        var result = _formatter.Format(data, Config(baseWageType: "=Basic, \"Pay\""));

        Text(result.Content).ShouldBe(Csv(Header, "42,'=Basic Pay,8.00,,,,,,,,"));
    }

    [Test]
    public void Format_Surcharge_IsSkippedAndCountedWhenNoSurchargeWageTypeConfigured()
    {
        var data = DataWith(Hours(PayrollEntryKind.Surcharge, 15, 2m));

        var result = _formatter.Format(data, Config(surchargeWageType: string.Empty));

        Text(result.Content).ShouldBe(Csv(Header));
        result.RecordCount.ShouldBe(0);
        result.EmittedEntryCount.ShouldBe(0);
        result.SkippedUnmappedSurchargeCount.ShouldBe(1);
    }

    [Test]
    public void Format_Surcharge_IsWrittenAsNormalHoursUnderSurchargeWageType()
    {
        var data = DataWith(Hours(PayrollEntryKind.Surcharge, 15, 2m));

        var result = _formatter.Format(data, Config(surchargeWageType: "Overtime Premium"));

        Text(result.Content).ShouldBe(Csv(Header, "42,Overtime Premium,2.00,,,,,,,,"));
        result.RecordCount.ShouldBe(1);
        result.EmittedEntryCount.ShouldBe(1);
    }

    [Test]
    public void Format_WorkHours_IsSkippedAndCountedWhenNoBaseWageTypeConfigured()
    {
        var data = DataWith(Hours(PayrollEntryKind.WorkHours, 15, 8m));

        var result = _formatter.Format(data, Config(baseWageType: "  "));

        Text(result.Content).ShouldBe(Csv(Header));
        result.EmittedEntryCount.ShouldBe(0);
        result.SkippedUnmappedBaseWageCount.ShouldBe(1);
    }

    [Test]
    public void Format_MappedAbsence_UsesConfiguredDescriptionAsNormalHours()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":{{\"ausfallschluessel\":\"\",\"wageType\":\"Sick Pay\"}}}}";
        var data = DataWith(Hours(PayrollEntryKind.Absence, 20, 8m, absenceId));

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));

        Text(result.Content).ShouldBe(Csv(Header, "42,Sick Pay,8.00,,,,,,,,"));
        result.RecordCount.ShouldBe(1);
        result.SkippedAbsenceCount.ShouldBe(0);
    }

    [Test]
    public void Format_MappedAbsenceWithBlankWageType_IsSkippedAndCountedAsAbsence()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":{{\"ausfallschluessel\":\"K\",\"wageType\":\"\"}}}}";
        var data = DataWith(Hours(PayrollEntryKind.Absence, 20, 8m, absenceId));

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));

        Text(result.Content).ShouldBe(Csv(Header));
        result.EmittedEntryCount.ShouldBe(0);
        result.SkippedAbsenceCount.ShouldBe(1);
    }

    [Test]
    public void Format_UnmappedAbsence_IsSkippedAndCounted()
    {
        var data = DataWith(Hours(PayrollEntryKind.Absence, 20, 8m, Guid.NewGuid()));

        var result = _formatter.Format(data, Config(absenceMappingJson: "{}"));

        Text(result.Content).ShouldBe(Csv(Header));
        result.RecordCount.ShouldBe(0);
        result.SkippedAbsenceCount.ShouldBe(1);
    }

    [Test]
    public void Format_DefaultsToUtf8_WhenEncodingUnset()
    {
        var result = _formatter.Format(DataWith(), Config(encoding: string.Empty));

        Text(result.Content, "utf-8").ShouldBe(Csv(Header));
    }

    [Test]
    public void Format_ExposesFormatKeyContentTypeAndExtension()
    {
        _formatter.FormatKey.ShouldBe(PayrollExportConstants.FormatKeyBrightpayIeUk);
        _formatter.ContentType.ShouldBe(PayrollExportConstants.ContentTypeCsv);
        _formatter.FileExtension.ShouldBe(PayrollExportConstants.FileExtensionCsv);
    }

    [Test]
    public void Format_OnCallDay_IsNotWrittenAsHoursAndIsCountedAsUnsupportedUnit()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":{{\"ausfallschluessel\":\"\",\"wageType\":\"On Call\"}}}}";
        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 18),
            Kind = PayrollEntryKind.Absence,
            Quantity = 1m,
            Unit = PayrollQuantityUnit.Days,
            AbsenceId = absenceId,
        });

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));

        Text(result.Content).ShouldBe(Csv(Header));
        result.RecordCount.ShouldBe(0);
        result.SkippedAbsenceCount.ShouldBe(0);
        result.SkippedUnsupportedUnitCount.ShouldBe(1);
    }
}
