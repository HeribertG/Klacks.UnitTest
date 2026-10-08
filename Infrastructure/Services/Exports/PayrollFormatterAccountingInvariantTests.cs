// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Invariant over every registered payroll formatter (found by reflection, so a new country pack is covered
/// automatically): each input entry - every kind including an unknown one, every quantity unit, mapped and unmapped
/// absences, with and without a surcharge wage type - is either emitted or counted in exactly one skip counter.
/// Also: an unparsable absence mapping is reported, never silently treated as an empty mapping, and no formatter
/// writes a day-based entry into an hours-only field.
/// </summary>
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Interfaces.Exports;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class PayrollFormatterAccountingInvariantTests
{
    private const PayrollEntryKind UnknownKind = (PayrollEntryKind)99;
    private const string InvalidMappingJson = "{ this is not json";

    private static readonly Guid MappedAbsenceId = Guid.NewGuid();
    private static readonly Guid UnmappedAbsenceId = Guid.NewGuid();
    private static readonly DateOnly PeriodStart = new(2026, 1, 1);
    private static readonly DateOnly PeriodEnd = new(2026, 1, 31);

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        System.Text.Encoding.RegisterProvider(System.Text.CodePagesEncodingProvider.Instance);
    }

    public static IEnumerable<TestCaseData> FormatterCases()
    {
        foreach (var formatter in AllFormatters())
        {
            foreach (var surchargeWageType in new[] { "1010", string.Empty })
            {
                yield return new TestCaseData(formatter, surchargeWageType)
                    .SetArgDisplayNames(formatter.FormatKey, surchargeWageType.Length == 0 ? "no-surcharge-wage" : "surcharge-wage");
            }
        }
    }

    public static IEnumerable<TestCaseData> FormatterOnlyCases()
    {
        return AllFormatters().Select(f => new TestCaseData(f).SetArgDisplayNames(f.FormatKey));
    }

    [TestCaseSource(nameof(FormatterCases))]
    public void EveryEntry_IsEmittedOrCountedExactlyOnce(IPayrollExportFormatter formatter, string surchargeWageType)
    {
        var entries = AllEntryCombinations().ToList();
        var data = DataWith(entries);

        var result = formatter.Format(data, Config(formatter.FormatKey, surchargeWageType, MappingJsonFor(formatter.FormatKey)));

        (result.EmittedEntryCount + result.TotalSkippedCount).ShouldBe(
            entries.Count,
            $"{formatter.FormatKey}: emitted {result.EmittedEntryCount}, skipped {result.TotalSkippedCount}");
        result.AbsenceMappingInvalid.ShouldBeFalse();
    }

    [TestCaseSource(nameof(FormatterOnlyCases))]
    public void InvalidAbsenceMapping_IsReported_AndEveryAbsenceCounted(IPayrollExportFormatter formatter)
    {
        var entries = new List<PayrollDayEntry>
        {
            Entry(PayrollEntryKind.Absence, PayrollQuantityUnit.Hours, MappedAbsenceId, 1),
            Entry(PayrollEntryKind.Absence, PayrollQuantityUnit.Days, MappedAbsenceId, 2),
        };

        var result = formatter.Format(DataWith(entries), Config(formatter.FormatKey, "1010", InvalidMappingJson));

        result.AbsenceMappingInvalid.ShouldBeTrue();
        result.SkippedAbsenceCount.ShouldBe(entries.Count);
    }

    [TestCase(PayrollExportConstants.FormatKeyMeritPalkEe)]
    [TestCase(PayrollExportConstants.FormatKeyBrightpayIeUk)]
    public void HoursOnlyLayouts_CountDayEntries_InsteadOfWritingThem(string formatKey)
    {
        var formatter = AllFormatters().Single(f => f.FormatKey == formatKey);
        var entries = new List<PayrollDayEntry> { Entry(PayrollEntryKind.Absence, PayrollQuantityUnit.Days, MappedAbsenceId, 3) };

        var result = formatter.Format(DataWith(entries), Config(formatKey, "1010", MappingJsonFor(formatKey)));

        result.EmittedEntryCount.ShouldBe(0);
        result.SkippedUnsupportedUnitCount.ShouldBe(1);
    }

    private static IEnumerable<PayrollDayEntry> AllEntryCombinations()
    {
        var day = 1;
        foreach (var kind in Enum.GetValues<PayrollEntryKind>().Append(UnknownKind))
        {
            foreach (var unit in Enum.GetValues<PayrollQuantityUnit>())
            {
                if (kind == PayrollEntryKind.Absence)
                {
                    yield return Entry(kind, unit, MappedAbsenceId, day++);
                    yield return Entry(kind, unit, UnmappedAbsenceId, day++);
                    yield return Entry(kind, unit, null, day++);
                }
                else
                {
                    yield return Entry(kind, unit, null, day++);
                }
            }
        }

        yield return Entry(PayrollEntryKind.WorkHours, PayrollQuantityUnit.Hours, null, 28);
        yield return Entry(PayrollEntryKind.Absence, PayrollQuantityUnit.Days, MappedAbsenceId, 28);
        yield return Entry(PayrollEntryKind.Absence, PayrollQuantityUnit.Hours, MappedAbsenceId, 28);
    }

    private static PayrollDayEntry Entry(PayrollEntryKind kind, PayrollQuantityUnit unit, Guid? absenceId, int day)
    {
        return new PayrollDayEntry
        {
            Date = PeriodStart.AddDays(day - 1),
            Kind = kind,
            Unit = unit,
            Quantity = unit == PayrollQuantityUnit.Days ? 1m : 4.5m,
            AbsenceId = absenceId,
        };
    }

    private static PayrollExportData DataWith(List<PayrollDayEntry> entries)
    {
        return new PayrollExportData
        {
            StartDate = PeriodStart,
            EndDate = PeriodEnd,
            Employees =
            [
                new PayrollEmployee
                {
                    ClientId = Guid.NewGuid(),
                    IdNumber = 42,
                    FullName = "Muster, Max",
                    Entries = entries,
                },
            ],
        };
    }

    private static PayrollExportGroupConfig Config(string formatKey, string surchargeWageType, string mappingJson)
    {
        return new PayrollExportGroupConfig
        {
            TargetSystem = formatKey,
            Delimiter = PayrollExportConstants.DefaultDelimiter,
            Encoding = PayrollExportConstants.DefaultEncoding,
            BaseWageType = "1000",
            SurchargeWageType = surchargeWageType,
            AbsenceMappingJson = mappingJson,
        };
    }

    private static string MappingJsonFor(string formatKey)
    {
        return formatKey == PayrollExportConstants.FormatKeyMeritPalkEe
            ? $"{{\"{MappedAbsenceId}\":\"PIK\"}}"
            : $"{{\"{MappedAbsenceId}\":{{\"WageType\":\"2000\",\"Ausfallschluessel\":\"K\",\"LonArt\":\"2000\",\"Kod\":\"K\"}}}}";
    }

    private static IEnumerable<IPayrollExportFormatter> AllFormatters()
    {
        return typeof(DatevLugBewegungsdatenFormatter).Assembly.GetTypes()
            .Where(t => typeof(IPayrollExportFormatter).IsAssignableFrom(t) && t is { IsAbstract: false, IsInterface: false })
            .OrderBy(t => t.FullName)
            .Select(t => (IPayrollExportFormatter)Activator.CreateInstance(t)!);
    }
}
