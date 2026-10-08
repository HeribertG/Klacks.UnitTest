// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the BrightPay "Import Daily Payments" formatter: header names, one row per mapped on-call day in
/// "Number of normal days", hour-based entries counted instead of written, unmapped absences counted.
/// </summary>
using System.Text;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class BrightpayIeUkDailyExportFormatterTests
{
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
        var lines = Encoding.UTF8.GetString(result.Content)
            .Split(PayrollExportConstants.LineEnding, StringSplitOptions.RemoveEmptyEntries);

        lines.ShouldBe(new[] { "Works number,Description,Number of normal days", "42,On Call,1.00" });
        result.EmittedEntryCount.ShouldBe(1);
        result.SkippedUnsupportedUnitCount.ShouldBe(1);
        result.SkippedAbsenceCount.ShouldBe(1);
    }

    [Test]
    public void FormatKey_IsTheDailyBrightpayKey()
    {
        _formatter.FormatKey.ShouldBe(PayrollExportConstants.FormatKeyBrightpayIeUkDaily);
    }

    private static PayrollExportGroupConfig Config()
    {
        return new PayrollExportGroupConfig
        {
            TargetSystem = PayrollExportConstants.FormatKeyBrightpayIeUkDaily,
            Delimiter = string.Empty,
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
