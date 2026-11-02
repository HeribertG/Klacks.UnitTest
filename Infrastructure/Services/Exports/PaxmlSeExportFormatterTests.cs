// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the PAXml 2.0 (Sweden) payroll export formatter: schema-valid XML structure,
/// wage-type mapping, anstid/datum/antal population, surcharge gating, absences as tidtrans (valid tidkod) or
/// lonetrans (other codes), both absence mapping shapes (LonArt and the common WageType) and skip accounting.
/// </summary>

using System.IO;
using System.Xml;
using System.Xml.Linq;
using System.Xml.Schema;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Services.Exports;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class PaxmlSeExportFormatterTests
{
    private const string SchemaFileName = "se-paxml-2.0.xsd";

    private PaxmlSeExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new PaxmlSeExportFormatter();
    }

    private static PayrollExportGroupConfig Config(
        string baseWageType = "1000",
        string surchargeWageType = "",
        string absenceMappingJson = "{}")
    {
        return new PayrollExportGroupConfig
        {
            TargetSystem = PayrollExportConstants.FormatKeyPaxmlSe,
            BaseWageType = baseWageType,
            SurchargeWageType = surchargeWageType,
            AbsenceMappingJson = absenceMappingJson,
        };
    }

    private static PayrollExportData DataWith(int idNumber, params PayrollDayEntry[] entries)
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
                    FullName = "Andersson, Anna",
                    Entries = entries.ToList(),
                },
            ],
        };
    }

    private static XDocument Parse(byte[] content)
    {
        using var stream = new MemoryStream(content);
        return XDocument.Load(stream);
    }

    private static string SchemaPath()
    {
        return Path.Combine(TestContext.CurrentContext.TestDirectory, "Infrastructure", "Services", "Exports", SchemaFileName);
    }

    private static List<string> ValidateAgainstSchema(byte[] content)
    {
        var schemaSet = new XmlSchemaSet();
        schemaSet.Add(null, SchemaPath());

        var settings = new XmlReaderSettings
        {
            ValidationType = ValidationType.Schema,
            Schemas = schemaSet,
        };

        var errors = new List<string>();
        settings.ValidationEventHandler += (_, args) => errors.Add(args.Message);

        using var stream = new MemoryStream(content);
        using var reader = XmlReader.Create(stream, settings);
        while (reader.Read())
        {
        }

        return errors;
    }

    private static void AssertValidAgainstSchema(byte[] content)
    {
        ValidateAgainstSchema(content).ShouldBeEmpty();
    }

    [Test]
    public void Format_WorkHours_ProducesSchemaValidDocumentWithPaxmlRoot()
    {
        var data = DataWith(
            42,
            new PayrollDayEntry
            {
                Date = new DateOnly(2026, 1, 15),
                Kind = PayrollEntryKind.WorkHours,
                Quantity = 8.5m,
            });

        var result = _formatter.Format(data, Config(baseWageType: "TID"));
        var document = Parse(result.Content);

        document.Root!.Name.LocalName.ShouldBe("paxml");
        AssertValidAgainstSchema(result.Content);
    }

    [Test]
    public void Format_WorkHours_PopulatesAnstidDatumAndAntalOnLonetrans()
    {
        var data = DataWith(
            42,
            new PayrollDayEntry
            {
                Date = new DateOnly(2026, 1, 15),
                Kind = PayrollEntryKind.WorkHours,
                Quantity = 8.5m,
            });

        var result = _formatter.Format(data, Config(baseWageType: "TID"));
        var document = Parse(result.Content);

        var lonetrans = document.Root!.Element("lonetransaktioner")!.Element("lonetrans")!;

        lonetrans.Attribute("anstid")!.Value.ShouldBe("42");
        lonetrans.Element("lonart")!.Value.ShouldBe("TID");
        lonetrans.Element("datum")!.Value.ShouldBe("2026-01-15");
        lonetrans.Element("antal")!.Value.ShouldBe("8.50");
        result.RecordCount.ShouldBe(1);
        result.SkippedAbsenceCount.ShouldBe(0);
    }

    [Test]
    public void Format_ProducesOneLonetransPerDayEntry()
    {
        var data = DataWith(
            42,
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 15), Kind = PayrollEntryKind.WorkHours, Quantity = 8m },
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 16), Kind = PayrollEntryKind.WorkHours, Quantity = 6m });

        var result = _formatter.Format(data, Config());
        var document = Parse(result.Content);

        var lonetransElements = document.Root!.Element("lonetransaktioner")!.Elements("lonetrans").ToList();

        lonetransElements.Count.ShouldBe(2);
        result.RecordCount.ShouldBe(2);
    }

    [Test]
    public void Format_Surcharge_IsOmittedWhenNoSurchargeWageTypeConfigured()
    {
        var data = DataWith(
            42,
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 15), Kind = PayrollEntryKind.Surcharge, Quantity = 2m });

        var result = _formatter.Format(data, Config(surchargeWageType: string.Empty));
        var document = Parse(result.Content);

        result.RecordCount.ShouldBe(0);
        document.Root!.Element("lonetransaktioner")!.Elements("lonetrans").ShouldBeEmpty();
    }

    [Test]
    public void Format_Surcharge_IsEmittedWithSurchargeWageTypeWhenConfigured()
    {
        var data = DataWith(
            42,
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 15), Kind = PayrollEntryKind.Surcharge, Quantity = 2m });

        var result = _formatter.Format(data, Config(surchargeWageType: "OB1"));
        var document = Parse(result.Content);
        var lonetrans = document.Root!.Element("lonetransaktioner")!.Element("lonetrans")!;

        result.RecordCount.ShouldBe(1);
        lonetrans.Element("lonart")!.Value.ShouldBe("OB1");
        lonetrans.Element("antal")!.Value.ShouldBe("2.00");
    }

    [Test]
    public void Format_MappedAbsenceWithNonTidkodLonArt_IsWrittenAsLonetrans()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":{{\"lonArt\":\"2000\"}}}}";

        var data = DataWith(
            42,
            new PayrollDayEntry
            {
                Date = new DateOnly(2026, 1, 20),
                Kind = PayrollEntryKind.Absence,
                Quantity = 8m,
                AbsenceId = absenceId,
            });

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));
        var document = Parse(result.Content);
        var lonetrans = document.Root!.Element("lonetransaktioner")!.Element("lonetrans")!;

        result.RecordCount.ShouldBe(1);
        result.SkippedAbsenceCount.ShouldBe(0);
        lonetrans.Element("lonart")!.Value.ShouldBe("2000");
        document.Root.Element("tidtransaktioner").ShouldBeNull();
        AssertValidAgainstSchema(result.Content);
    }

    [Test]
    public void Format_MappedAbsenceWithTidkod_IsWrittenAsTidtrans_ExactOutput()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":{{\"WageType\":\"SJK\",\"Ausfallschluessel\":\"K\"}}}}";

        var data = DataWith(
            42,
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 15), Kind = PayrollEntryKind.WorkHours, Quantity = 8.5m },
            new PayrollDayEntry
            {
                Date = new DateOnly(2026, 1, 20),
                Kind = PayrollEntryKind.Absence,
                Quantity = 8m,
                AbsenceId = absenceId,
            });
        data.ExportDate = new DateTime(2026, 2, 1, 8, 0, 0, DateTimeKind.Utc);

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));

        System.Text.Encoding.UTF8.GetString(result.Content).ShouldBe(
            "<?xml version=\"1.0\" encoding=\"utf-8\"?>\r\n"
            + "<paxml>\r\n"
            + "  <header>\r\n"
            + "    <version>2.0</version>\r\n"
            + "    <datum>2026-02-01T08:00:00</datum>\r\n"
            + "  </header>\r\n"
            + "  <tidtransaktioner>\r\n"
            + "    <tidtrans anstid=\"42\">\r\n"
            + "      <tidkod>SJK</tidkod>\r\n"
            + "      <datum>2026-01-20</datum>\r\n"
            + "      <timmar>8.00</timmar>\r\n"
            + "    </tidtrans>\r\n"
            + "  </tidtransaktioner>\r\n"
            + "  <lonetransaktioner>\r\n"
            + "    <lonetrans anstid=\"42\">\r\n"
            + "      <lonart>1000</lonart>\r\n"
            + "      <datum>2026-01-15</datum>\r\n"
            + "      <antal>8.50</antal>\r\n"
            + "    </lonetrans>\r\n"
            + "  </lonetransaktioner>\r\n"
            + "</paxml>");
        result.RecordCount.ShouldBe(2);
        result.EmittedEntryCount.ShouldBe(2);
        result.TotalSkippedCount.ShouldBe(0);
        AssertValidAgainstSchema(result.Content);
    }

    [Test]
    public void Format_TidkodWithNonAsciiLetter_IsWrittenAsSchemaValidTidtrans()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":{{\"LonArt\":\"SEM_FÖR\"}}}}";

        var data = DataWith(
            42,
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 20), Kind = PayrollEntryKind.Absence, Quantity = 4m, AbsenceId = absenceId });

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));
        var tidtrans = Parse(result.Content).Root!.Element("tidtransaktioner")!.Element("tidtrans")!;

        tidtrans.Element("tidkod")!.Value.ShouldBe("SEM_FÖR");
        tidtrans.Element("timmar")!.Value.ShouldBe("4.00");
        AssertValidAgainstSchema(result.Content);
    }

    [Test]
    public void Format_LowercaseTidkod_IsNotATidkod_AndFallsBackToLonetrans()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":{{\"WageType\":\"sjk\"}}}}";

        var data = DataWith(
            42,
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 20), Kind = PayrollEntryKind.Absence, Quantity = 8m, AbsenceId = absenceId });

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));
        var document = Parse(result.Content);

        document.Root!.Element("tidtransaktioner").ShouldBeNull();
        document.Root.Element("lonetransaktioner")!.Element("lonetrans")!.Element("lonart")!.Value.ShouldBe("sjk");
        AssertValidAgainstSchema(result.Content);
    }

    [Test]
    public void Format_DayBasedAbsenceWithTidkod_IsWrittenAsLonetransWithDayCount()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":{{\"WageType\":\"SJK\"}}}}";

        var data = DataWith(
            42,
            new PayrollDayEntry
            {
                Date = new DateOnly(2026, 1, 18),
                Kind = PayrollEntryKind.Absence,
                Quantity = 1m,
                Unit = PayrollQuantityUnit.Days,
                AbsenceId = absenceId,
            });

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));
        var document = Parse(result.Content);
        var lonetrans = document.Root!.Element("lonetransaktioner")!.Element("lonetrans")!;

        document.Root.Element("tidtransaktioner").ShouldBeNull();
        lonetrans.Element("lonart")!.Value.ShouldBe("SJK");
        lonetrans.Element("antal")!.Value.ShouldBe("1.00");
        result.RecordCount.ShouldBe(1);
    }

    [Test]
    public void Format_CommonMappingShape_UsesWageType_AndLonArtWinsWhenBothAreSet()
    {
        var commonId = Guid.NewGuid();
        var bothId = Guid.NewGuid();
        var mapping = $"{{\"{commonId}\":{{\"WageType\":\"2000\",\"Ausfallschluessel\":\"K\"}},"
            + $"\"{bothId}\":{{\"WageType\":\"2000\",\"LonArt\":\"2100\"}}}}";

        var data = DataWith(
            42,
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 20), Kind = PayrollEntryKind.Absence, Quantity = 8m, AbsenceId = commonId },
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 21), Kind = PayrollEntryKind.Absence, Quantity = 8m, AbsenceId = bothId });

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));
        var lonarts = Parse(result.Content).Root!.Element("lonetransaktioner")!.Elements("lonetrans")
            .Select(e => e.Element("lonart")!.Value).ToList();

        lonarts.ShouldBe(new List<string> { "2000", "2100" });
        result.SkippedAbsenceCount.ShouldBe(0);
    }

    [TestCase("{{\"{0}\":{{\"WageType\":\"\"}}}}")]
    [TestCase("{{\"{0}\":{{\"LonArt\":\"  \",\"WageType\":\" \"}}}}")]
    [TestCase("{{\"{0}\":{{\"Ausfallschluessel\":\"K\"}}}}")]
    public void Format_AbsenceMappedToEmptyCode_IsSkippedAndCounted(string mappingTemplate)
    {
        var absenceId = Guid.NewGuid();
        var mapping = string.Format(System.Globalization.CultureInfo.InvariantCulture, mappingTemplate, absenceId);

        var data = DataWith(
            42,
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 20), Kind = PayrollEntryKind.Absence, Quantity = 8m, AbsenceId = absenceId });

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));
        var document = Parse(result.Content);

        result.RecordCount.ShouldBe(0);
        result.SkippedAbsenceCount.ShouldBe(1);
        document.Root!.Element("lonetransaktioner")!.Elements("lonetrans").ShouldBeEmpty();
        document.Root.Element("tidtransaktioner").ShouldBeNull();
    }

    [TestCase("")]
    [TestCase("   ")]
    public void Format_WorkHoursWithEmptyBaseWageType_IsSkippedAndCounted(string baseWageType)
    {
        var data = DataWith(
            42,
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 15), Kind = PayrollEntryKind.WorkHours, Quantity = 8m });

        var result = _formatter.Format(data, Config(baseWageType: baseWageType));

        Parse(result.Content).Root!.Element("lonetransaktioner")!.Elements("lonetrans").ShouldBeEmpty();
        result.RecordCount.ShouldBe(0);
        result.SkippedUnmappedBaseWageCount.ShouldBe(1);
        AssertValidAgainstSchema(result.Content);
    }

    [Test]
    public void Format_SurchargeWithWhitespaceSurchargeWageType_IsSkippedAndCounted()
    {
        var data = DataWith(
            42,
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 15), Kind = PayrollEntryKind.Surcharge, Quantity = 2m });

        var result = _formatter.Format(data, Config(surchargeWageType: "  "));

        result.RecordCount.ShouldBe(0);
        result.SkippedUnmappedSurchargeCount.ShouldBe(1);
    }

    [Test]
    public void Format_UnmappedAbsence_IsSkippedAndCounted()
    {
        var data = DataWith(
            42,
            new PayrollDayEntry
            {
                Date = new DateOnly(2026, 1, 20),
                Kind = PayrollEntryKind.Absence,
                Quantity = 8m,
                AbsenceId = Guid.NewGuid(),
            });

        var result = _formatter.Format(data, Config(absenceMappingJson: "{}"));
        var document = Parse(result.Content);

        result.RecordCount.ShouldBe(0);
        result.SkippedAbsenceCount.ShouldBe(1);
        document.Root!.Element("lonetransaktioner")!.Elements("lonetrans").ShouldBeEmpty();
    }

    [Test]
    public void ValidateAgainstSchema_RejectsDocumentWithInvalidVersionEnum()
    {
        var invalidDocument = System.Text.Encoding.UTF8.GetBytes(
            "<paxml><header><version>9.9</version></header></paxml>");

        var errors = ValidateAgainstSchema(invalidDocument);

        errors.ShouldNotBeEmpty();
    }

    [Test]
    public void Format_ExposesFormatKeyContentTypeAndExtension()
    {
        _formatter.FormatKey.ShouldBe(PayrollExportConstants.FormatKeyPaxmlSe);
        _formatter.ContentType.ShouldBe(PayrollExportConstants.ContentTypeXml);
        _formatter.FileExtension.ShouldBe(PayrollExportConstants.FileExtensionXml);
    }
}
