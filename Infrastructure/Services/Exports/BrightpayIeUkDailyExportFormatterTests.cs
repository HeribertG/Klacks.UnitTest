// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the BrightPay "Import Daily Payments" formatter: "Employee works number" plus five
/// "Payment n - Description" / "Payment n - Number of normal days" blocks, mapped on-call days summed per description
/// into one row per employee, fixed comma delimiter, hour-based entries counted instead of written, unmapped absences
/// counted.
/// </summary>
using System.Text;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class BrightpayIeUkDailyExportFormatterTests
{
    private const string Header =
        "Employee works number," +
        "Payment 1 - Description,Payment 1 - Number of normal days," +
        "Payment 2 - Description,Payment 2 - Number of normal days," +
        "Payment 3 - Description,Payment 3 - Number of normal days," +
        "Payment 4 - Description,Payment 4 - Number of normal days," +
        "Payment 5 - Description,Payment 5 - Number of normal days";

    private static readonly Guid OnCallAbsenceId = Guid.NewGuid();

    private BrightpayIeUkDailyExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new BrightpayIeUkDailyExportFormatter();
    }

    [Test]
    public void Format_OnCallDay_IsWrittenAsNormalDay_HourEntriesAreCounted()
    {
        var data = DataWith(
            new PayrollDayEntry
            {
                Date = new DateOnly(2026, 1, 18),
                Kind = PayrollEntryKind.Absence,
                Quantity = 1m,
                Unit = PayrollQuantityUnit.Days,
                AbsenceId = OnCallAbsenceId,
            },
            new PayrollDayEntry
            {
                Date = new DateOnly(2026, 1, 19),
                Kind = PayrollEntryKind.WorkHours,
                Quantity = 8m,
            },
            new PayrollDayEntry
            {
                Date = new DateOnly(2026, 1, 20),
                Kind = PayrollEntryKind.Absence,
                Quantity = 1m,
                Unit = PayrollQuantityUnit.Days,
                AbsenceId = Guid.NewGuid(),
            });

        var result = _formatter.Format(data, Config());

        Encoding.UTF8.GetString(result.Content).ShouldBe(Csv(Header, "42,On Call,1.00,,,,,,,,"));
        result.RecordCount.ShouldBe(1);
        result.EmittedEntryCount.ShouldBe(1);
        result.SkippedUnsupportedUnitCount.ShouldBe(1);
        result.SkippedAbsenceCount.ShouldBe(1);
    }

    [Test]
    public void Format_SeveralOnCallDays_AreSummedIntoOneRow_AndConfiguredDelimiterIsIgnored()
    {
        var data = DataWith(
            OnCallDay(18),
            OnCallDay(19),
            OnCallDay(25));

        var result = _formatter.Format(data, Config(delimiter: ";"));

        Encoding.UTF8.GetString(result.Content).ShouldBe(Csv(Header, "42,On Call,3.00,,,,,,,,"));
        result.RecordCount.ShouldBe(1);
        result.EmittedEntryCount.ShouldBe(3);
        result.TotalSkippedCount.ShouldBe(0);
    }

    [Test]
    public void Format_NoDayEntries_WritesOnlyTheHeaderRow()
    {
        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 19),
            Kind = PayrollEntryKind.Surcharge,
            Quantity = 2m,
        });

        var result = _formatter.Format(data, Config());

        Encoding.UTF8.GetString(result.Content).ShouldBe(Csv(Header));
        result.RecordCount.ShouldBe(0);
        result.SkippedUnsupportedUnitCount.ShouldBe(1);
    }

    [Test]
    public void Format_MappedDayAbsenceWithBlankWageType_IsSkippedAndCountedAsAbsence()
    {
        var config = Config();
        config.AbsenceMappingJson = $"{{\"{OnCallAbsenceId}\":{{\"ausfallschluessel\":\"K\",\"wageType\":\" \"}}}}";

        var result = _formatter.Format(DataWith(OnCallDay(18)), config);

        Encoding.UTF8.GetString(result.Content).ShouldBe(Csv(Header));
        result.EmittedEntryCount.ShouldBe(0);
        result.SkippedAbsenceCount.ShouldBe(1);
    }

    [Test]
    public void FormatKey_IsTheDailyBrightpayKey()
    {
        _formatter.FormatKey.ShouldBe(PayrollExportConstants.FormatKeyBrightpayIeUkDaily);
    }

    private static string Csv(params string[] lines)
    {
        return string.Concat(lines.Select(line => line + PayrollExportConstants.LineEnding));
    }

    private static PayrollDayEntry OnCallDay(int day)
    {
        return new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, day),
            Kind = PayrollEntryKind.Absence,
            Quantity = 1m,
            Unit = PayrollQuantityUnit.Days,
            AbsenceId = OnCallAbsenceId,
        };
    }

    private static PayrollExportGroupConfig Config(string delimiter = "")
    {
        return new PayrollExportGroupConfig
        {
            TargetSystem = PayrollExportConstants.FormatKeyBrightpayIeUkDaily,
            Delimiter = delimiter,
            Encoding = "utf-8",
            AbsenceMappingJson = $"{{\"{OnCallAbsenceId}\":{{\"ausfallschluessel\":\"\",\"wageType\":\"On Call\"}}}}",
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
                    FullName = "Muster, Max",
                    Entries = entries.ToList(),
                },
            ],
        };
    }
}
