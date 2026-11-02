// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the WinMENTOR (Romania) "Import pontaje" workbook formatter: sheet name,
/// identity columns, the fixed 31 day-of-month columns (also for short months), mapped absence codes
/// (numeric legend codes as number cells, other codes as text), surcharges kept out of the worked hours,
/// unmapped-absence skipping and out-of-month entry accounting.
/// </summary>

using ClosedXML.Excel;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Services.Exports;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class WinmentorRoExportFormatterTests
{
    private const int IdentityColumnCount = 5;
    private const int FirstDataRow = 3;

    private WinmentorRoExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new WinmentorRoExportFormatter();
    }

    private static PayrollExportGroupConfig Config(string absenceMappingJson = "{}")
    {
        return new PayrollExportGroupConfig
        {
            TargetSystem = PayrollExportConstants.FormatKeyWinmentorRo,
            AbsenceMappingJson = absenceMappingJson,
        };
    }

    private static PayrollExportData DataWith(DateOnly start, DateOnly end, params PayrollDayEntry[] entries)
    {
        return new PayrollExportData
        {
            StartDate = start,
            EndDate = end,
            Employees =
            [
                new PayrollEmployee
                {
                    ClientId = Guid.NewGuid(),
                    IdNumber = 7,
                    FullName = "Popescu, Ion",
                    Entries = entries.ToList(),
                },
            ],
        };
    }

    private static IXLWorksheet OpenSheet(byte[] content)
    {
        using var stream = new MemoryStream(content);
        var workbook = new XLWorkbook(stream);
        return workbook.Worksheet(1);
    }

    [Test]
    public void Format_UsesPontajSheetName()
    {
        var data = DataWith(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));
        var result = _formatter.Format(data, Config());
        var sheet = OpenSheet(result.Content);

        sheet.Name.ShouldBe("pontaj");
    }

    [Test]
    public void Format_WritesHeaderRowAndIdentityColumns()
    {
        var data = DataWith(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));
        var result = _formatter.Format(data, Config());
        var sheet = OpenSheet(result.Content);

        sheet.Cell(1, 1).GetString().ShouldBe("An");
        sheet.Cell(1, 2).GetString().ShouldBe("Luna");
        sheet.Cell(1, 3).GetString().ShouldBe("Simbol formatie");
        sheet.Cell(1, 4).GetString().ShouldBe("Identi ficator formatie");
        sheet.Cell(1, IdentityColumnCount).GetString().ShouldBe("Marca");
    }

    [Test]
    public void Format_WritesDayOfMonthHeadersForEachDayInPeriod()
    {
        var data = DataWith(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));
        var result = _formatter.Format(data, Config());
        var sheet = OpenSheet(result.Content);

        sheet.Cell(1, IdentityColumnCount + 1).GetValue<int>().ShouldBe(1);
        sheet.Cell(1, IdentityColumnCount + 31).GetValue<int>().ShouldBe(31);
    }

    [Test]
    public void Format_WritesAvansAndLichidareSubHeaders()
    {
        var data = DataWith(new DateOnly(2026, 7, 1), new DateOnly(2026, 7, 31));
        var result = _formatter.Format(data, Config());
        var sheet = OpenSheet(result.Content);

        var avansStart = IdentityColumnCount + 31 + 1;
        sheet.Cell(1, avansStart).GetString().ShouldBe("AVANS");
        sheet.Cell(2, avansStart).GetString().ShouldBe("zile lucrate");
        sheet.Cell(2, avansStart + 1).GetString().ShouldBe("ore suplim.I");
        sheet.Cell(2, avansStart + 2).GetString().ShouldBe("ore supllim.II");
        sheet.Cell(2, avansStart + 3).GetString().ShouldBe("ore noapte");

        var lichidareStart = avansStart + 4;
        sheet.Cell(1, lichidareStart).GetString().ShouldBe("LICHIDARE");
        sheet.Cell(2, lichidareStart).GetString().ShouldBe("zile lucrate");

        var observationsColumn = lichidareStart + 4;
        sheet.Cell(2, observationsColumn).GetString().ShouldBe("OBSERVATII:");
    }

    [Test]
    public void Format_WritesPersonnelNumberAndDailyWorkHours()
    {
        var data = DataWith(
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            new PayrollDayEntry { Date = new DateOnly(2026, 7, 1), Kind = PayrollEntryKind.WorkHours, Quantity = 8m },
            new PayrollDayEntry { Date = new DateOnly(2026, 7, 20), Kind = PayrollEntryKind.WorkHours, Quantity = 6.5m });

        var result = _formatter.Format(data, Config());
        var sheet = OpenSheet(result.Content);

        sheet.Cell(FirstDataRow, IdentityColumnCount).GetValue<int>().ShouldBe(7);
        sheet.Cell(FirstDataRow, IdentityColumnCount + 1).GetValue<decimal>().ShouldBe(8m);
        sheet.Cell(FirstDataRow, IdentityColumnCount + 20).GetValue<decimal>().ShouldBe(6.5m);
        sheet.Cell(FirstDataRow, IdentityColumnCount + 2).IsEmpty().ShouldBeTrue();
        result.RecordCount.ShouldBe(1);
        result.SkippedAbsenceCount.ShouldBe(0);
    }

    [Test]
    public void Format_Surcharge_IsNotAddedToWorkedHoursAndIsCountedAsUnmappedSurcharge()
    {
        var data = DataWith(
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            new PayrollDayEntry { Date = new DateOnly(2026, 7, 5), Kind = PayrollEntryKind.WorkHours, Quantity = 8.5m },
            new PayrollDayEntry { Date = new DateOnly(2026, 7, 5), Kind = PayrollEntryKind.Surcharge, Quantity = 1.25m });

        var result = _formatter.Format(data, Config());
        var sheet = OpenSheet(result.Content);

        sheet.Cell(FirstDataRow, IdentityColumnCount + 5).GetValue<decimal>().ShouldBe(8.5m);
        sheet.Cell(FirstDataRow, "AO").GetValue<int>().ShouldBe(1);
        result.EmittedEntryCount.ShouldBe(1);
        result.SkippedUnmappedSurchargeCount.ShouldBe(1);
        result.SkippedSupersededCount.ShouldBe(0);
    }

    [Test]
    public void Format_SurchargeOnlyDay_LeavesCellBlankAndIsNotAWorkedDay()
    {
        var data = DataWith(
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            new PayrollDayEntry { Date = new DateOnly(2026, 7, 6), Kind = PayrollEntryKind.Surcharge, Quantity = 2m });

        var result = _formatter.Format(data, Config());
        var sheet = OpenSheet(result.Content);

        sheet.Cell(FirstDataRow, IdentityColumnCount + 6).IsEmpty().ShouldBeTrue();
        sheet.Cell(FirstDataRow, "AO").GetValue<int>().ShouldBe(0);
        result.EmittedEntryCount.ShouldBe(0);
        result.SkippedUnmappedSurchargeCount.ShouldBe(1);
    }

    [Test]
    public void Format_MappedNumericAbsenceCode_IsWrittenAsNumber()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":{{\"ausfallschluessel\":\"33\",\"wageType\":\"\"}}}}";

        var data = DataWith(
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            new PayrollDayEntry
            {
                Date = new DateOnly(2026, 7, 10),
                Kind = PayrollEntryKind.Absence,
                Quantity = 8m,
                AbsenceId = absenceId,
            });

        var result = _formatter.Format(data, Config(mapping));
        var sheet = OpenSheet(result.Content);

        var cell = sheet.Cell(FirstDataRow, IdentityColumnCount + 10);
        cell.DataType.ShouldBe(XLDataType.Number);
        cell.GetValue<int>().ShouldBe(33);
        result.EmittedEntryCount.ShouldBe(1);
        result.SkippedAbsenceCount.ShouldBe(0);
    }

    [Test]
    public void Format_MappedAbsence_WritesAbsenceCodeInsteadOfHours()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":{{\"ausfallschluessel\":\"CO\",\"wageType\":\"\"}}}}";

        var data = DataWith(
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            new PayrollDayEntry
            {
                Date = new DateOnly(2026, 7, 10),
                Kind = PayrollEntryKind.Absence,
                Quantity = 8m,
                AbsenceId = absenceId,
            });

        var result = _formatter.Format(data, Config(mapping));
        var sheet = OpenSheet(result.Content);

        var cell = sheet.Cell(FirstDataRow, IdentityColumnCount + 10);
        cell.DataType.ShouldBe(XLDataType.Text);
        cell.GetString().ShouldBe("CO");
        result.SkippedAbsenceCount.ShouldBe(0);
    }

    [Test]
    public void Format_UnmappedAbsence_IsCountedAndCellFallsBackToBlank()
    {
        var data = DataWith(
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            new PayrollDayEntry
            {
                Date = new DateOnly(2026, 7, 12),
                Kind = PayrollEntryKind.Absence,
                Quantity = 8m,
                AbsenceId = Guid.NewGuid(),
            });

        var result = _formatter.Format(data, Config());
        var sheet = OpenSheet(result.Content);

        result.SkippedAbsenceCount.ShouldBe(1);
        sheet.Cell(FirstDataRow, IdentityColumnCount + 12).IsEmpty().ShouldBeTrue();
    }

    [Test]
    public void Format_LeavesAvansEmptyAndCountsAllWorkedDaysIntoLichidare()
    {
        var data = DataWith(
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            new PayrollDayEntry { Date = new DateOnly(2026, 7, 10), Kind = PayrollEntryKind.WorkHours, Quantity = 8m },
            new PayrollDayEntry { Date = new DateOnly(2026, 7, 15), Kind = PayrollEntryKind.WorkHours, Quantity = 8m },
            new PayrollDayEntry { Date = new DateOnly(2026, 7, 20), Kind = PayrollEntryKind.WorkHours, Quantity = 8m });

        var result = _formatter.Format(data, Config());
        var sheet = OpenSheet(result.Content);

        var avansStart = IdentityColumnCount + 31 + 1;
        var lichidareStart = avansStart + 4;

        sheet.Cell(FirstDataRow, avansStart).IsEmpty().ShouldBeTrue();
        sheet.Cell(FirstDataRow, lichidareStart).GetValue<int>().ShouldBe(3);
    }

    [Test]
    public void Format_ShortMonth_KeepsFixed31DayGridAndSummaryColumnPositions()
    {
        var data = DataWith(
            new DateOnly(2026, 2, 1),
            new DateOnly(2026, 2, 28),
            new PayrollDayEntry { Date = new DateOnly(2026, 2, 3), Kind = PayrollEntryKind.WorkHours, Quantity = 8m },
            new PayrollDayEntry { Date = new DateOnly(2026, 2, 20), Kind = PayrollEntryKind.WorkHours, Quantity = 8m });

        var result = _formatter.Format(data, Config());
        var sheet = OpenSheet(result.Content);

        sheet.Cell(1, IdentityColumnCount + 31).GetValue<int>().ShouldBe(31);
        sheet.Cell("AK1").GetString().ShouldBe("AVANS");
        sheet.Cell("AO1").GetString().ShouldBe("LICHIDARE");
        sheet.Cell("AS2").GetString().ShouldBe("OBSERVATII:");
        sheet.Cell(FirstDataRow, IdentityColumnCount + 29).IsEmpty().ShouldBeTrue();
        sheet.Cell(FirstDataRow, IdentityColumnCount + 31).IsEmpty().ShouldBeTrue();
        sheet.Cell(FirstDataRow, "AK").IsEmpty().ShouldBeTrue();
        sheet.Cell(FirstDataRow, "AO").GetValue<int>().ShouldBe(2);
    }

    [Test]
    public void Format_PeriodNotStartingOnFirst_PlacesHoursInDayOfMonthColumn()
    {
        var data = DataWith(
            new DateOnly(2026, 7, 10),
            new DateOnly(2026, 7, 31),
            new PayrollDayEntry { Date = new DateOnly(2026, 7, 12), Kind = PayrollEntryKind.WorkHours, Quantity = 7.5m });

        var result = _formatter.Format(data, Config());
        var sheet = OpenSheet(result.Content);

        sheet.Cell(1, IdentityColumnCount + 1).GetValue<int>().ShouldBe(1);
        sheet.Cell(FirstDataRow, IdentityColumnCount + 12).GetValue<decimal>().ShouldBe(7.5m);
        sheet.Cell(FirstDataRow, IdentityColumnCount + 3).IsEmpty().ShouldBeTrue();
    }

    [Test]
    public void Format_EntryOutsideStartMonth_IsNotWrittenAndCountedAsSuperseded()
    {
        var data = DataWith(
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 8, 5),
            new PayrollDayEntry { Date = new DateOnly(2026, 7, 2), Kind = PayrollEntryKind.WorkHours, Quantity = 8m },
            new PayrollDayEntry { Date = new DateOnly(2026, 8, 2), Kind = PayrollEntryKind.WorkHours, Quantity = 5m });

        var result = _formatter.Format(data, Config());
        var sheet = OpenSheet(result.Content);

        sheet.Cell(FirstDataRow, IdentityColumnCount + 2).GetValue<decimal>().ShouldBe(8m);
        sheet.Cell(FirstDataRow, "AK").IsEmpty().ShouldBeTrue();
        sheet.Cell(FirstDataRow, "AO").GetValue<int>().ShouldBe(1);
        result.EmittedEntryCount.ShouldBe(1);
        result.SkippedSupersededCount.ShouldBe(1);
    }

    [Test]
    public void Format_ExposesFormatKeyContentTypeAndExtension()
    {
        _formatter.FormatKey.ShouldBe(PayrollExportConstants.FormatKeyWinmentorRo);
        _formatter.ContentType.ShouldBe(ExportConstants.ContentTypeXlsx);
        _formatter.FileExtension.ShouldBe(".xlsx");
    }
}
