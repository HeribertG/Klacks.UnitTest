// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the DATEV Lohn und Gehalt Bewegungsdaten formatter (Zeitwirtschaftsschnittstelle): header line
/// with the payroll month, month bookings (Wert, aggregated per employee and Lohnart) versus calendar bookings
/// (2-digit Kalendertag, Ausfallschluessel, Stunden- or Tagesanzahl), one payroll month per file, Lohnart and
/// Personalnummer range checks and the skip counters for every entry that is not written.
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
    private const string Crlf = "\r\n";
    private const string JanuaryHeader = ";;01/2026" + Crlf;

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
        return DataFor(42, entries);
    }

    private static PayrollExportData DataFor(int idNumber, params PayrollDayEntry[] entries)
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
                    IdNumber = idNumber,
                    FullName = "Muster, Max",
                    Entries = entries.ToList(),
                },
            ],
        };
    }

    private static PayrollDayEntry Work(int day, decimal hours)
    {
        return new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, day),
            Kind = PayrollEntryKind.WorkHours,
            Quantity = hours,
        };
    }

    private static PayrollDayEntry Surcharge(int day, decimal hours)
    {
        return new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, day),
            Kind = PayrollEntryKind.Surcharge,
            Quantity = hours,
        };
    }

    private static PayrollDayEntry Absence(Guid absenceId, int day, decimal quantity, PayrollQuantityUnit unit = PayrollQuantityUnit.Hours)
    {
        return new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, day),
            Kind = PayrollEntryKind.Absence,
            Quantity = quantity,
            Unit = unit,
            AbsenceId = absenceId,
        };
    }

    private static string Mapping(Guid absenceId, string ausfallschluessel, string wageType)
    {
        return $"{{\"{absenceId}\":{{\"ausfallschluessel\":\"{ausfallschluessel}\",\"wageType\":\"{wageType}\"}}}}";
    }

    private static string Decode(byte[] content)
    {
        return Encoding.GetEncoding(PayrollExportConstants.Windows1252CodePage).GetString(content);
    }

    private static void ShouldBeConsistent(PayrollExportResult result, int inputCount)
    {
        (result.EmittedEntryCount + result.TotalSkippedCount).ShouldBe(inputCount);
    }

    [Test]
    public void Format_NoEntries_WritesOnlyHeaderWithPayrollMonth()
    {
        var result = _formatter.Format(DataWith(), Config());

        Decode(result.Content).ShouldBe(JanuaryHeader);
        result.RecordCount.ShouldBe(0);
    }

    [Test]
    public void Format_HeaderMonth_IsTakenFromStartDate()
    {
        var data = DataWith();
        data.StartDate = new DateOnly(2026, 11, 1);
        data.EndDate = new DateOnly(2026, 11, 30);

        Decode(_formatter.Format(data, Config()).Content).ShouldBe(";;11/2026" + Crlf);
    }

    [Test]
    public void Format_WorkHours_AreAggregatedIntoOneMonthBookingWithWert()
    {
        var data = DataWith(Work(15, 8.5m), Work(16, 7.25m), Work(17, 0.005m));

        var result = _formatter.Format(data, Config(baseWageType: "1000"));

        Decode(result.Content).ShouldBe(JanuaryHeader + "42;;;1000;;;15,76;;;;" + Crlf);
        result.RecordCount.ShouldBe(1);
        result.EmittedEntryCount.ShouldBe(3);
        ShouldBeConsistent(result, 3);
    }

    [Test]
    public void Format_MonthBooking_HasElevenFieldsWithDayKeyHoursAndDaysEmpty()
    {
        var result = _formatter.Format(DataWith(Work(15, 8m)), Config());

        var record = Decode(result.Content).Split(Crlf)[1];
        var fields = record.Split(PayrollExportConstants.DefaultDelimiter);

        fields.Length.ShouldBe(PayrollExportConstants.DatevLugFieldCount);
        fields[1].ShouldBeEmpty();
        fields[2].ShouldBeEmpty();
        fields[4].ShouldBeEmpty();
        fields[5].ShouldBeEmpty();
        fields[6].ShouldBe("8,00");
    }

    [Test]
    public void Format_WorkAndSurcharge_AreSeparateMonthBookingsInFirstSeenOrder()
    {
        var data = DataWith(Surcharge(3, 2m), Work(3, 8m), Surcharge(4, 1.5m), Work(4, 8m));

        var result = _formatter.Format(data, Config(surchargeWageType: "1500"));

        Decode(result.Content).ShouldBe(
            JanuaryHeader
            + "42;;;1500;;;3,50;;;;" + Crlf
            + "42;;;1000;;;16,00;;;;" + Crlf);
        result.RecordCount.ShouldBe(2);
        ShouldBeConsistent(result, 4);
    }

    [Test]
    public void Format_MultipleEmployees_AggregatePerEmployee()
    {
        var data = DataWith(Work(5, 8m));
        data.Employees.Add(new PayrollEmployee
        {
            ClientId = Guid.NewGuid(),
            IdNumber = 7,
            Entries = [Work(5, 4m), Work(6, 4m)],
        });

        var result = _formatter.Format(data, Config());

        Decode(result.Content).ShouldBe(
            JanuaryHeader
            + "42;;;1000;;;8,00;;;;" + Crlf
            + "7;;;1000;;;8,00;;;;" + Crlf);
    }

    [Test]
    public void Format_Surcharge_IsSkippedAndCountedWhenNoSurchargeWageTypeConfigured()
    {
        var result = _formatter.Format(DataWith(Surcharge(15, 2m)), Config(surchargeWageType: string.Empty));

        Decode(result.Content).ShouldBe(JanuaryHeader);
        result.RecordCount.ShouldBe(0);
        result.SkippedUnmappedSurchargeCount.ShouldBe(1);
        ShouldBeConsistent(result, 1);
    }

    [Test]
    public void Format_WorkHours_AreSkippedAndCountedWhenNoBaseWageTypeConfigured()
    {
        var result = _formatter.Format(DataWith(Work(15, 8m)), Config(baseWageType: string.Empty));

        Decode(result.Content).ShouldBe(JanuaryHeader);
        result.SkippedUnmappedBaseWageCount.ShouldBe(1);
        ShouldBeConsistent(result, 1);
    }

    [TestCase("0")]
    [TestCase("6000")]
    [TestCase("7999")]
    [TestCase("10000")]
    [TestCase("12a")]
    [TestCase("-1")]
    public void Format_WageTypeOutsideDatevRange_IsCountedAsUnmapped(string wageType)
    {
        var data = DataWith(Work(15, 8m), Surcharge(15, 1m));

        var result = _formatter.Format(data, Config(baseWageType: wageType, surchargeWageType: wageType));

        Decode(result.Content).ShouldBe(JanuaryHeader);
        result.SkippedUnmappedBaseWageCount.ShouldBe(1);
        result.SkippedUnmappedSurchargeCount.ShouldBe(1);
        ShouldBeConsistent(result, 2);
    }

    [TestCase("1", "1")]
    [TestCase("5999", "5999")]
    [TestCase("8000", "8000")]
    [TestCase("9999", "9999")]
    [TestCase(" 0200 ", "200")]
    public void Format_WageTypeInsideDatevRange_IsWrittenAsNumber(string wageType, string expected)
    {
        var result = _formatter.Format(DataWith(Work(15, 8m)), Config(baseWageType: wageType));

        Decode(result.Content).ShouldBe(JanuaryHeader + $"42;;;{expected};;;8,00;;;;" + Crlf);
    }

    [Test]
    public void Format_MappedAbsenceWithAusfallschluessel_IsCalendarBookingWithTwoDigitDay()
    {
        var absenceId = Guid.NewGuid();
        var data = DataWith(Absence(absenceId, 5, 8m), Absence(absenceId, 20, 4m));

        var result = _formatter.Format(data, Config(absenceMappingJson: Mapping(absenceId, "K", "2000")));

        Decode(result.Content).ShouldBe(
            JanuaryHeader
            + "42;05;K;2000;8,00;;;;;;" + Crlf
            + "42;20;K;2000;4,00;;;;;;" + Crlf);
        result.RecordCount.ShouldBe(2);
        result.SkippedAbsenceCount.ShouldBe(0);
        ShouldBeConsistent(result, 2);
    }

    [Test]
    public void Format_DayBasedAbsenceWithAusfallschluessel_WritesTagesanzahl()
    {
        var absenceId = Guid.NewGuid();
        var data = DataWith(Absence(absenceId, 9, 1m, PayrollQuantityUnit.Days));

        var result = _formatter.Format(data, Config(absenceMappingJson: Mapping(absenceId, "U", "3000")));

        Decode(result.Content).ShouldBe(JanuaryHeader + "42;09;U;3000;;1,00;;;;;" + Crlf);
    }

    [Test]
    public void Format_MappedAbsenceWithoutAusfallschluessel_IsMonthBookingWithWert()
    {
        var onCallId = Guid.NewGuid();
        var data = DataWith(
            Absence(onCallId, 18, 1m, PayrollQuantityUnit.Days),
            Absence(onCallId, 25, 1m, PayrollQuantityUnit.Days));

        var result = _formatter.Format(data, Config(absenceMappingJson: Mapping(onCallId, string.Empty, "4711")));

        Decode(result.Content).ShouldBe(JanuaryHeader + "42;;;4711;;;2,00;;;;" + Crlf);
        result.RecordCount.ShouldBe(1);
        ShouldBeConsistent(result, 2);
    }

    [Test]
    public void Format_MonthBookingsComeBeforeCalendarBookingsPerEmployee()
    {
        var absenceId = Guid.NewGuid();
        var data = DataWith(Absence(absenceId, 2, 8m), Work(3, 8m));

        var result = _formatter.Format(data, Config(absenceMappingJson: Mapping(absenceId, "K", "2000")));

        Decode(result.Content).ShouldBe(
            JanuaryHeader
            + "42;;;1000;;;8,00;;;;" + Crlf
            + "42;02;K;2000;8,00;;;;;;" + Crlf);
    }

    [Test]
    public void Format_UnmappedAbsence_IsSkippedAndCounted()
    {
        var result = _formatter.Format(DataWith(Absence(Guid.NewGuid(), 20, 8m)), Config(absenceMappingJson: "{}"));

        Decode(result.Content).ShouldBe(JanuaryHeader);
        result.RecordCount.ShouldBe(0);
        result.SkippedAbsenceCount.ShouldBe(1);
        ShouldBeConsistent(result, 1);
    }

    [TestCase("")]
    [TestCase("6500")]
    public void Format_AbsenceWithEmptyOrInvalidWageType_IsSkippedAndCounted(string wageType)
    {
        var absenceId = Guid.NewGuid();

        var result = _formatter.Format(
            DataWith(Absence(absenceId, 20, 8m)),
            Config(absenceMappingJson: Mapping(absenceId, "K", wageType)));

        Decode(result.Content).ShouldBe(JanuaryHeader);
        result.SkippedAbsenceCount.ShouldBe(1);
        ShouldBeConsistent(result, 1);
    }

    [Test]
    public void Format_AbsenceWithAusfallschluesselLongerThanTwoCharacters_IsSkippedAndCounted()
    {
        var absenceId = Guid.NewGuid();

        var result = _formatter.Format(
            DataWith(Absence(absenceId, 20, 8m)),
            Config(absenceMappingJson: Mapping(absenceId, "KRK", "2000")));

        Decode(result.Content).ShouldBe(JanuaryHeader);
        result.SkippedAbsenceCount.ShouldBe(1);
    }

    [Test]
    public void Format_DayBasedWorkHours_AreCountedAsUnsupportedUnit()
    {
        var entry = Work(5, 1m);
        entry.Unit = PayrollQuantityUnit.Days;

        var result = _formatter.Format(DataWith(entry), Config());

        Decode(result.Content).ShouldBe(JanuaryHeader);
        result.SkippedUnsupportedUnitCount.ShouldBe(1);
        ShouldBeConsistent(result, 1);
    }

    [Test]
    public void Format_EntriesOutsideHeaderMonth_AreSkippedAndCounted()
    {
        var data = DataWith(
            Work(31, 8m),
            new PayrollDayEntry { Date = new DateOnly(2026, 2, 1), Kind = PayrollEntryKind.WorkHours, Quantity = 8m },
            new PayrollDayEntry { Date = new DateOnly(2025, 12, 31), Kind = PayrollEntryKind.WorkHours, Quantity = 8m });
        data.EndDate = new DateOnly(2026, 2, 28);

        var result = _formatter.Format(data, Config());

        Decode(result.Content).ShouldBe(JanuaryHeader + "42;;;1000;;;8,00;;;;" + Crlf);
        result.EmittedEntryCount.ShouldBe(1);
        result.SkippedSupersededCount.ShouldBe(2);
        ShouldBeConsistent(result, 3);
    }

    [TestCase(0)]
    [TestCase(100000)]
    public void Format_PersonnelNumberOutsideDatevRange_SkipsAndCountsAllEntriesOfEmployee(int idNumber)
    {
        var data = DataFor(idNumber, Work(5, 8m), Surcharge(5, 1m));

        var result = _formatter.Format(data, Config(surchargeWageType: "1500"));

        Decode(result.Content).ShouldBe(JanuaryHeader);
        result.RecordCount.ShouldBe(0);
        result.SkippedSupersededCount.ShouldBe(2);
        ShouldBeConsistent(result, 2);
    }

    [Test]
    public void Format_PersonnelNumberAtUpperBound_IsWritten()
    {
        var result = _formatter.Format(DataFor(99999, Work(5, 8m)), Config());

        Decode(result.Content).ShouldBe(JanuaryHeader + "99999;;;1000;;;8,00;;;;" + Crlf);
    }

    [Test]
    public void Format_UnknownKind_IsCountedAsUnsupportedKind()
    {
        var entry = Work(5, 8m);
        entry.Kind = (PayrollEntryKind)99;

        var result = _formatter.Format(DataWith(entry), Config());

        result.SkippedUnsupportedKindCount.ShouldBe(1);
        ShouldBeConsistent(result, 1);
    }

    [Test]
    public void Format_ConfiguredDelimiter_IsUsedForHeaderAndRecords()
    {
        var config = Config();
        config.Delimiter = "|";

        var result = _formatter.Format(DataWith(Work(5, 8m)), config);

        Decode(result.Content).ShouldBe("||01/2026" + Crlf + "42|||1000|||8,00||||" + Crlf);
    }

    [Test]
    public void Format_ExposesFormatKeyContentTypeAndTxtExtension()
    {
        _formatter.FormatKey.ShouldBe(PayrollExportConstants.FormatKeyDatevLug);
        _formatter.ContentType.ShouldBe("text/plain");
        _formatter.FileExtension.ShouldBe(".txt");
    }
}
