// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the Abacus AbaConnect "LOHN FlatPreEntry" CH formatter: XML envelope structure,
/// field population, surcharge gating and unmapped-absence skipping.
/// </summary>

using System.Text;
using System.Xml.Linq;
using System.Xml.XPath;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Services.Exports;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class AbaConnectChExportFormatterTests
{
    private const string PreEntryXPath = "Task/Transaction/PreEntry";

    private AbaConnectChExportFormatter _formatter = null!;

    [SetUp]
    public void Setup()
    {
        _formatter = new AbaConnectChExportFormatter();
    }

    private static PayrollExportGroupConfig Config(
        string baseWageType = "1000",
        string surchargeWageType = "",
        string absenceMappingJson = "{}")
    {
        return new PayrollExportGroupConfig
        {
            GroupId = Guid.NewGuid(),
            TargetSystem = PayrollExportConstants.FormatKeyAbaconnectCh,
            BaseWageType = baseWageType,
            SurchargeWageType = surchargeWageType,
            AbsenceMappingJson = absenceMappingJson,
        };
    }

    private static PayrollExportData DataWith(params PayrollDayEntry[] entries)
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
                    IdNumber = 42,
                    FullName = "Muster, Max",
                    Entries = entries.ToList(),
                },
            ],
        };
    }

    private static XDocument Parse(byte[] content)
    {
        return XDocument.Parse(Encoding.UTF8.GetString(content));
    }

    [Test]
    public void Format_ExposesFormatKeyContentTypeAndExtension()
    {
        _formatter.FormatKey.ShouldBe(PayrollExportConstants.FormatKeyAbaconnectCh);
        _formatter.ContentType.ShouldBe(PayrollExportConstants.ContentTypeXml);
        _formatter.FileExtension.ShouldBe(PayrollExportConstants.FileExtensionXml);
    }

    [Test]
    public void Format_WorkHours_ProducesEnvelopeWithTaskCountAndParameterBlock()
    {
        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.WorkHours,
            Quantity = 8.5m,
        });

        var result = _formatter.Format(data, Config());
        var xml = Parse(result.Content);

        xml.Root!.Name.LocalName.ShouldBe("AbaConnectContainer");
        xml.Root.Element("TaskCount")!.Value.ShouldBe("1");

        var parameter = xml.Root.Element("Task")!.Element("Parameter")!;
        parameter.Element("Application")!.Value.ShouldBe("LOHN");
        parameter.Element("Id")!.Value.ShouldBe("FlatPreEntry");
        parameter.Element("MapId")!.Value.ShouldBe("AbaDefault");
        parameter.Element("Version")!.Value.ShouldBe("2020.00");
    }

    [Test]
    public void Format_WorkHours_PopulatesEmployeeNumberPeriodDatePayrollTypeAmountAndFactor()
    {
        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.WorkHours,
            Quantity = 8.5m,
        });

        var result = _formatter.Format(data, Config(baseWageType: "1000"));
        var xml = Parse(result.Content);
        var preEntry = xml.Root!.XPathSelectElement(PreEntryXPath);

        preEntry.ShouldNotBeNull();
        preEntry!.Attribute("mode")!.Value.ShouldBe("SAVE");
        preEntry.Element("EmployeeNumber")!.Value.ShouldBe("42");
        preEntry.Element("PeriodDate")!.Value.ShouldBe("2026-01-15");
        preEntry.Element("PayrollType")!.Value.ShouldBe("1000");
        preEntry.Element("Amount")!.Value.ShouldBe("8.500000");
        preEntry.Element("Factor")!.Value.ShouldBe("1.000000");

        result.RecordCount.ShouldBe(1);
        result.SkippedAbsenceCount.ShouldBe(0);
    }

    [Test]
    public void Format_Surcharge_IsOmittedWhenNoSurchargeWageTypeConfigured()
    {
        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.Surcharge,
            Quantity = 2m,
        });

        var result = _formatter.Format(data, Config(surchargeWageType: string.Empty));
        var xml = Parse(result.Content);

        result.RecordCount.ShouldBe(0);
        xml.Root!.XPathSelectElements(PreEntryXPath).ShouldBeEmpty();
    }

    [Test]
    public void Format_Surcharge_IsEmittedWithSurchargeWageTypeWhenConfigured()
    {
        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.Surcharge,
            Quantity = 2m,
        });

        var result = _formatter.Format(data, Config(surchargeWageType: "1500"));
        var xml = Parse(result.Content);
        var preEntry = xml.Root!.XPathSelectElement(PreEntryXPath);

        result.RecordCount.ShouldBe(1);
        preEntry!.Element("PayrollType")!.Value.ShouldBe("1500");
        preEntry.Element("Amount")!.Value.ShouldBe("2.000000");
    }

    [Test]
    public void Format_MappedAbsence_UsesConfiguredWageType()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":{{\"ausfallschluessel\":\"01\",\"wageType\":\"2000\"}}}}";

        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 20),
            Kind = PayrollEntryKind.Absence,
            Quantity = 8m,
            AbsenceId = absenceId,
        });

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));
        var xml = Parse(result.Content);
        var preEntry = xml.Root!.XPathSelectElement(PreEntryXPath);

        result.RecordCount.ShouldBe(1);
        result.SkippedAbsenceCount.ShouldBe(0);
        preEntry!.Element("PayrollType")!.Value.ShouldBe("2000");
        preEntry.Element("Amount")!.Value.ShouldBe("8.000000");
    }

    [Test]
    public void Format_UnmappedAbsence_IsSkippedAndCounted()
    {
        var data = DataWith(new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 20),
            Kind = PayrollEntryKind.Absence,
            Quantity = 8m,
            AbsenceId = Guid.NewGuid(),
        });

        var result = _formatter.Format(data, Config(absenceMappingJson: "{}"));
        var xml = Parse(result.Content);

        result.RecordCount.ShouldBe(0);
        result.SkippedAbsenceCount.ShouldBe(1);
        xml.Root!.XPathSelectElements(PreEntryXPath).ShouldBeEmpty();
    }

    [Test]
    public void Format_MultipleEntries_ProducesOneTaskWithRepeatedPreEntryElements()
    {
        var data = DataWith(
            new PayrollDayEntry
            {
                Date = new DateOnly(2026, 1, 15),
                Kind = PayrollEntryKind.WorkHours,
                Quantity = 8m,
            },
            new PayrollDayEntry
            {
                Date = new DateOnly(2026, 1, 16),
                Kind = PayrollEntryKind.WorkHours,
                Quantity = 7m,
            });

        var result = _formatter.Format(data, Config());
        var xml = Parse(result.Content);

        xml.Root!.Elements("Task").Count().ShouldBe(1);
        xml.Root!.XPathSelectElements(PreEntryXPath).Count().ShouldBe(2);
        result.RecordCount.ShouldBe(2);
    }
}
