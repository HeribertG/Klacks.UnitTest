// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the PAXml 2.0 (Sweden) payroll export formatter: schema-valid XML structure,
/// wage-type mapping, anstid/datum/antal population, surcharge gating and unmapped-absence
/// skipping.
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
            GroupId = Guid.NewGuid(),
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
            GroupId = Guid.NewGuid(),
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
    public void Format_MappedAbsence_UsesLonArtFromMapping()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":{{\"lonArt\":\"SJK\"}}}}";

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
        lonetrans.Element("lonart")!.Value.ShouldBe("SJK");
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
