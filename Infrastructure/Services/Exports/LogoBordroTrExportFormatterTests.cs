// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for LogoBordroTrExportFormatter verifying the Logo "Excel'den Puantaj Bilgileri Aktarımı" three-row
/// structure (row 1 description, row 2 five-digit XYZTV reference codes, row 3+ one row per sicil), the identity
/// codes 00001 (Sicil No) / 90002 (Ad) / 90003 (Soyad) stored as text, that puantaj columns and their row-2 codes come
/// from the group config ordered by code, that codes outside the puantaj scheme are rejected as unmapped, that the
/// code's V digit (2 Gün / 3 Saat) must match the entry unit, per-employee aggregation, and name splitting.
/// </summary>
using System.IO;
using ClosedXML.Excel;
using Shouldly;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Services.Exports;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class LogoBordroTrExportFormatterTests
{
    private const string BaseWageType = "01013";
    private const string SurchargeWageType = "02013";
    private const string AbsenceWageType = "01023";
    private const string OnCallDayWageType = "04012";
    private static readonly Guid MappedAbsenceId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private LogoBordroTrExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new LogoBordroTrExportFormatter();
    }

    [Test]
    public void FormatKey_And_ContentType_AreLogoXlsx()
    {
        _formatter.FormatKey.ShouldBe(PayrollExportConstants.FormatKeyLogoBordroTr);
        _formatter.ContentType.ShouldBe(ExportConstants.ContentTypeXlsx);
        _formatter.FileExtension.ShouldBe(".xlsx");
    }

    [Test]
    public void Format_EmitsThreeRowStructure_WithIdentityHeadersAndCodes()
    {
        var sheet = OpenSheet(_formatter.Format(BuildData(), Config()));

        sheet.Name.ShouldBe("Puantaj");
        sheet.Cell(1, 1).GetString().ShouldBe("Sicil Numarası");
        sheet.Cell(1, 2).GetString().ShouldBe("Ad");
        sheet.Cell(1, 3).GetString().ShouldBe("Soyad");

        sheet.Cell(2, 1).GetString().ShouldBe("00001");
        sheet.Cell(2, 2).GetString().ShouldBe("90002");
        sheet.Cell(2, 3).GetString().ShouldBe("90003");
    }

    [Test]
    public void Format_ReferenceCodes_AreTextKeepingLeadingZeros()
    {
        var sheet = OpenSheet(_formatter.Format(BuildData(), Config()));

        sheet.Cell(2, 1).DataType.ShouldBe(XLDataType.Text);
        sheet.Cell(2, 4).DataType.ShouldBe(XLDataType.Text);
        sheet.Cell(2, 4).GetString().ShouldBe(BaseWageType);
    }

    [Test]
    public void Format_DescriptionRow_NamesPuantajGroupRecordAndUnit()
    {
        var sheet = OpenSheet(_formatter.Format(BuildData(), Config()));

        sheet.Cell(1, 4).GetString().ShouldBe("Mesai 01 Saat");
        sheet.Cell(1, 5).GetString().ShouldBe("Ek Mesai 01 Saat");
    }

    [Test]
    public void Format_WageColumns_AreOrderedByReferenceCode()
    {
        var config = Config();
        config.BaseWageType = "02013";
        config.SurchargeWageType = "01013";

        var sheet = OpenSheet(_formatter.Format(BuildData(), config));

        sheet.Cell(2, 4).GetString().ShouldBe("01013");
        sheet.Cell(2, 5).GetString().ShouldBe("02013");
        sheet.Cell(3, 4).GetValue<decimal>().ShouldBe(1.5m);
        sheet.Cell(3, 5).GetValue<decimal>().ShouldBe(16m);
    }

    [TestCase("1000")]
    [TestCase("91001")]
    [TestCase("00001")]
    [TestCase("01014")]
    [TestCase("06013")]
    [TestCase("01003")]
    [TestCase("")]
    public void Format_BaseCodeOutsidePuantajScheme_IsUnmappedAndHasNoColumn(string invalidCode)
    {
        var config = Config();
        config.BaseWageType = invalidCode;

        var result = _formatter.Format(BuildData(), config);
        var sheet = OpenSheet(result);

        result.SkippedUnmappedBaseWageCount.ShouldBe(2);
        result.EmittedEntryCount.ShouldBe(1);
        sheet.Cell(2, 4).GetString().ShouldBe(SurchargeWageType);
        sheet.Cell(2, 5).GetString().ShouldBeEmpty();
    }

    [Test]
    public void Format_WageColumnCodes_ComeFromConfig()
    {
        var sheet = OpenSheet(_formatter.Format(BuildData(), Config()));

        sheet.Cell(2, 4).GetString().ShouldBe(BaseWageType);
        sheet.Cell(2, 5).GetString().ShouldBe(SurchargeWageType);
    }

    [Test]
    public void Format_AggregatesHoursPerEmployeePerWageType()
    {
        var sheet = OpenSheet(_formatter.Format(BuildData(), Config()));

        sheet.Cell(3, 1).GetValue<int>().ShouldBe(42);
        sheet.Cell(3, 2).GetString().ShouldBe("Mehmet");
        sheet.Cell(3, 3).GetString().ShouldBe("Yilmaz");
        sheet.Cell(3, 4).GetValue<decimal>().ShouldBe(16m);
        sheet.Cell(3, 5).GetValue<decimal>().ShouldBe(1.5m);
    }

    [Test]
    public void Format_WritesCompoundFirstName_FromStructuredFields()
    {
        var data = BuildData();
        data.Employees[0].FirstName = "Mehmet Ali";
        data.Employees[0].LastName = "Kaya";

        var sheet = OpenSheet(_formatter.Format(data, Config()));

        sheet.Cell(3, 2).GetString().ShouldBe("Mehmet Ali");
        sheet.Cell(3, 3).GetString().ShouldBe("Kaya");
    }

    [Test]
    public void Format_SkipsUnmappedAbsence_NotEmitted()
    {
        var data = BuildData();
        data.Employees[0].Entries.Add(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 7),
            Kind = PayrollEntryKind.Absence,
            Quantity = 8m,
            AbsenceId = Guid.NewGuid(),
        });

        var result = _formatter.Format(data, Config());
        var sheet = OpenSheet(result);

        result.SkippedAbsenceCount.ShouldBe(1);
        sheet.Cell(2, 6).GetString().ShouldBeEmpty();
    }

    [Test]
    public void Format_MappedAbsence_BecomesOwnWageColumn()
    {
        var data = BuildData();
        data.Employees[0].Entries.Add(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 8),
            Kind = PayrollEntryKind.Absence,
            Quantity = 8m,
            AbsenceId = MappedAbsenceId,
        });

        var config = Config(
            $"{{\"{MappedAbsenceId}\":{{\"WageType\":\"{AbsenceWageType}\",\"Ausfallschluessel\":\"K\"}}}}");
        var sheet = OpenSheet(_formatter.Format(data, config));

        sheet.Cell(2, 4).GetString().ShouldBe(BaseWageType);
        sheet.Cell(2, 5).GetString().ShouldBe(AbsenceWageType);
        sheet.Cell(2, 6).GetString().ShouldBe(SurchargeWageType);
        sheet.Cell(3, 5).GetValue<decimal>().ShouldBe(8m);
        sheet.Cell(3, 6).GetValue<decimal>().ShouldBe(1.5m);
    }

    [Test]
    public void Format_DayEntryOnACodeThatAlsoCarriesHours_IsCountedNotAdded()
    {
        var onCallId = Guid.NewGuid();
        var mapping = $"{{\"{onCallId}\":{{\"ausfallschluessel\":\"\",\"wageType\":\"{BaseWageType}\"}}}}";
        var data = new PayrollExportData
        {
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 31),
            Employees =
            [
                new PayrollEmployee
                {
                    ClientId = Guid.NewGuid(),
                    IdNumber = 7,
                    FullName = "Ayse Yilmaz",
                    FirstName = "Ayse",
                    LastName = "Yilmaz",
                    Entries =
                    [
                        new PayrollDayEntry { Date = new DateOnly(2026, 1, 5), Kind = PayrollEntryKind.WorkHours, Quantity = 8m },
                        new PayrollDayEntry
                        {
                            Date = new DateOnly(2026, 1, 11),
                            Kind = PayrollEntryKind.Absence,
                            Quantity = 1m,
                            Unit = PayrollQuantityUnit.Days,
                            AbsenceId = onCallId,
                        },
                    ],
                },
            ],
        };

        var result = _formatter.Format(data, Config(mapping));

        result.EmittedEntryCount.ShouldBe(1);
        result.SkippedUnsupportedUnitCount.ShouldBe(1);
    }

    [Test]
    public void Format_DayEntryOnAGunCode_IsWrittenAsDays()
    {
        var onCallId = Guid.NewGuid();
        var mapping = $"{{\"{onCallId}\":{{\"ausfallschluessel\":\"\",\"wageType\":\"{OnCallDayWageType}\"}}}}";
        var data = BuildData();
        data.Employees[0].Entries.Add(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 11),
            Kind = PayrollEntryKind.Absence,
            Quantity = 1m,
            Unit = PayrollQuantityUnit.Days,
            AbsenceId = onCallId,
        });
        data.Employees[0].Entries.Add(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 18),
            Kind = PayrollEntryKind.Absence,
            Quantity = 1m,
            Unit = PayrollQuantityUnit.Days,
            AbsenceId = onCallId,
        });

        var result = _formatter.Format(data, Config(mapping));
        var sheet = OpenSheet(result);

        sheet.Cell(1, 6).GetString().ShouldBe("Ek Ödeme 01 Gün");
        sheet.Cell(2, 6).GetString().ShouldBe(OnCallDayWageType);
        sheet.Cell(3, 6).GetValue<decimal>().ShouldBe(2m);
        result.SkippedUnsupportedUnitCount.ShouldBe(0);
    }

    [Test]
    public void Format_HourEntryOnAGunCode_IsCountedNotAdded()
    {
        var config = Config();
        config.BaseWageType = "01012";

        var result = _formatter.Format(BuildData(), config);
        var sheet = OpenSheet(result);

        result.SkippedUnsupportedUnitCount.ShouldBe(2);
        result.EmittedEntryCount.ShouldBe(1);
        sheet.Cell(2, 4).GetString().ShouldBe("01012");
        sheet.Cell(3, 4).IsEmpty().ShouldBeTrue();
    }
    private static IXLWorksheet OpenSheet(PayrollExportResult result)
    {
        using var stream = new MemoryStream(result.Content);
        var workbook = new XLWorkbook(stream);
        return workbook.Worksheet(1);
    }

    private static PayrollExportGroupConfig Config(string absenceMappingJson = "{}")
    {
        return new PayrollExportGroupConfig
        {
            TargetSystem = PayrollExportConstants.FormatKeyLogoBordroTr,
            BaseWageType = BaseWageType,
            SurchargeWageType = SurchargeWageType,
            AbsenceMappingJson = absenceMappingJson,
        };
    }

    private static PayrollExportData BuildData()
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
                    FullName = "Yilmaz, Mehmet",
                    FirstName = "Mehmet",
                    LastName = "Yilmaz",
                    Entries =
                    [
                        new PayrollDayEntry { Date = new DateOnly(2026, 1, 5), Kind = PayrollEntryKind.WorkHours, Quantity = 8m },
                        new PayrollDayEntry { Date = new DateOnly(2026, 1, 6), Kind = PayrollEntryKind.WorkHours, Quantity = 8m },
                        new PayrollDayEntry { Date = new DateOnly(2026, 1, 5), Kind = PayrollEntryKind.Surcharge, Quantity = 1.5m },
                    ],
                },
            ],
        };
    }
}
