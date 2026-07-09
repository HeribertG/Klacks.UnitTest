// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the WinMENTOR (Romania) "Import pontaje" workbook formatter: sheet name,
/// identity columns, day-of-month columns, mapped absence codes and unmapped-absence skipping.
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
            GroupId = Guid.NewGuid(),
            TargetSystem = PayrollExportConstants.FormatKeyWinmentorRo,
            AbsenceMappingJson = absenceMappingJson,
        };
    }

    private static PayrollExportData DataWith(DateOnly start, DateOnly end, params PayrollDayEntry[] entries)
    {
        return new PayrollExportData
        {
            GroupId = Guid.NewGuid(),
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
    public void Format_AddsSurchargeQuantityIntoTheSameDayCellAsWorkHours()
    {
        var data = DataWith(
            new DateOnly(2026, 7, 1),
            new DateOnly(2026, 7, 31),
            new PayrollDayEntry { Date = new DateOnly(2026, 7, 5), Kind = PayrollEntryKind.WorkHours, Quantity = 8m },
            new PayrollDayEntry { Date = new DateOnly(2026, 7, 5), Kind = PayrollEntryKind.Surcharge, Quantity = 2m });

        var result = _formatter.Format(data, Config());
        var sheet = OpenSheet(result.Content);

        sheet.Cell(FirstDataRow, IdentityColumnCount + 5).GetValue<decimal>().ShouldBe(10m);
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

        sheet.Cell(FirstDataRow, IdentityColumnCount + 10).GetString().ShouldBe("CO");
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
    public void Format_CountsWorkedDaysIntoAvansAndLichidareSplitAtDayFifteen()
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

        sheet.Cell(FirstDataRow, avansStart).GetValue<int>().ShouldBe(2);
        sheet.Cell(FirstDataRow, lichidareStart).GetValue<int>().ShouldBe(1);
    }

    [Test]
    public void Format_ExposesFormatKeyContentTypeAndExtension()
    {
        _formatter.FormatKey.ShouldBe(PayrollExportConstants.FormatKeyWinmentorRo);
        _formatter.ContentType.ShouldBe(ExportConstants.ContentTypeXlsx);
        _formatter.FileExtension.ShouldBe(".xlsx");
    }
}
