// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the Stormware PAMICA/POHODA dochazka_zamestnance formatter: root element and version
/// attribute, cislo_pracovniho_pomeru matching key, presence-vs-absence section assignment, unmapped-
/// absence skipping, single-employee guard and Windows-1250 encoding round-trip.
/// </summary>
using System.Text;
using System.Xml.Linq;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Services.Exports;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class PohodaCzExportFormatterTests
{
    private const string DochazkaZamestnance = "dochazka_zamestnance";
    private const string Hlavicka = "hlavicka";
    private const string CisloPracovnihoPomeru = "cislo_pracovniho_pomeru";
    private const string Nepritomnosti = "nepritomnosti";
    private const string Nepritomnost = "nepritomnost";
    private const string Pritomnost = "pritomnost";
    private const string PrescasPracovniDen = "prescas_pracovni_den";
    private const string Hodiny = "hodiny";
    private const string Kod = "kod";
    private const string Od = "od";
    private const string Do = "do";
    private const string Jmeno = "jmeno";
    private const string Prijmeni = "prijmeni";
    private const string Version = "version";

    private PohodaCzExportFormatter _formatter = null!;

    [OneTimeSetUp]
    public void OneTimeSetup()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [SetUp]
    public void Setup()
    {
        _formatter = new PohodaCzExportFormatter();
    }

    private static PayrollExportGroupConfig Config(
        string surchargeWageType = "",
        string absenceMappingJson = "{}")
    {
        return new PayrollExportGroupConfig
        {
            GroupId = Guid.NewGuid(),
            TargetSystem = PayrollExportConstants.FormatKeyPohodaCz,
            SurchargeWageType = surchargeWageType,
            AbsenceMappingJson = absenceMappingJson,
        };
    }

    private static PayrollExportData DataWith(string fullName, params PayrollDayEntry[] entries)
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
                    FullName = fullName,
                    Entries = entries.ToList(),
                },
            ],
        };
    }

    private static XDocument Parse(byte[] content)
    {
        var text = Encoding.GetEncoding(PayrollExportConstants.Windows1250CodePage).GetString(content);
        return XDocument.Parse(text);
    }

    [Test]
    public void Format_ProducesRootElementWithVersionAttribute()
    {
        var data = DataWith("Muster, Max", new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.WorkHours,
            Quantity = 8m,
        });

        var result = _formatter.Format(data, Config());
        var document = Parse(result.Content);

        document.Root.ShouldNotBeNull();
        document.Root!.Name.LocalName.ShouldBe(DochazkaZamestnance);
        document.Root.Attribute(Version)!.Value.ShouldBe("2.0");
    }

    [Test]
    public void Format_UsesEmployeeIdNumberAsCisloPracovnihoPomeru()
    {
        var data = DataWith("Muster, Max", new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.WorkHours,
            Quantity = 8m,
        });

        var result = _formatter.Format(data, Config());
        var document = Parse(result.Content);

        document.Root!.Element(Hlavicka)!.Element(CisloPracovnihoPomeru)!.Value.ShouldBe("42");
    }

    [Test]
    public void Format_WorkHours_IsSummedUnderPritomnostAndNotUnderNepritomnosti()
    {
        var data = DataWith(
            "Muster, Max",
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 15), Kind = PayrollEntryKind.WorkHours, Quantity = 8.5m },
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 16), Kind = PayrollEntryKind.WorkHours, Quantity = 4m });

        var result = _formatter.Format(data, Config());
        var document = Parse(result.Content);

        var pritomnost = document.Root!.Element(Pritomnost)!;
        pritomnost.Element(PrescasPracovniDen)!.Element(Hodiny)!.Value.ShouldBe("12:30");
        document.Root.Element(Nepritomnosti)!.Elements(Nepritomnost).ShouldBeEmpty();
        result.RecordCount.ShouldBe(2);
    }

    [Test]
    public void Format_MappedAbsence_IsEmittedUnderNepritomnostiWithKodAndOdDo()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":{{\"kod\":\"N01\"}}}}";

        var data = DataWith("Muster, Max", new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 20),
            Kind = PayrollEntryKind.Absence,
            Quantity = 8m,
            AbsenceId = absenceId,
        });

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));
        var document = Parse(result.Content);

        var nepritomnost = document.Root!.Element(Nepritomnosti)!.Element(Nepritomnost)!;
        nepritomnost.Element(Kod)!.Value.ShouldBe("N01");
        nepritomnost.Element(Od)!.Value.ShouldBe("2026-01-20");
        nepritomnost.Element(Do)!.Value.ShouldBe("2026-01-20");
        document.Root.Element(Pritomnost)!.Elements().ShouldBeEmpty();
        result.RecordCount.ShouldBe(1);
        result.SkippedAbsenceCount.ShouldBe(0);
    }

    [Test]
    public void Format_UnmappedAbsence_IsSkippedAndCountedAndNotEmitted()
    {
        var data = DataWith("Muster, Max", new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 20),
            Kind = PayrollEntryKind.Absence,
            Quantity = 8m,
            AbsenceId = Guid.NewGuid(),
        });

        var result = _formatter.Format(data, Config(absenceMappingJson: "{}"));
        var document = Parse(result.Content);

        document.Root!.Element(Nepritomnosti)!.Elements(Nepritomnost).ShouldBeEmpty();
        result.RecordCount.ShouldBe(0);
        result.SkippedAbsenceCount.ShouldBe(1);
    }

    [Test]
    public void Format_Surcharge_IsOmittedWhenNoSurchargeWageTypeConfigured()
    {
        var data = DataWith("Muster, Max", new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.Surcharge,
            Quantity = 2m,
        });

        var result = _formatter.Format(data, Config(surchargeWageType: string.Empty));

        result.RecordCount.ShouldBe(0);
    }

    [Test]
    public void Format_Surcharge_IsEmittedAsPriplatekWhenSurchargeWageTypeConfigured()
    {
        var data = DataWith("Muster, Max", new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.Surcharge,
            Quantity = 2.25m,
        });

        var result = _formatter.Format(data, Config(surchargeWageType: "P07"));
        var document = Parse(result.Content);

        var priplatek = document.Root!.Element("mzdy")!.Element("priplatek")!;
        priplatek.Element(Kod)!.Value.ShouldBe("P07");
        priplatek.Element(Hodiny)!.Value.ShouldBe("2:15");
        result.RecordCount.ShouldBe(1);
    }

    [Test]
    public void Format_MultipleEmployees_ThrowsNotSupported()
    {
        var data = new PayrollExportData
        {
            GroupId = Guid.NewGuid(),
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 31),
            Employees =
            [
                new PayrollEmployee { ClientId = Guid.NewGuid(), IdNumber = 1, FullName = "A, A" },
                new PayrollEmployee { ClientId = Guid.NewGuid(), IdNumber = 2, FullName = "B, B" },
            ],
        };

        Should.Throw<NotSupportedException>(() => _formatter.Format(data, Config()));
    }

    [Test]
    public void Format_EncodesCzechDiacriticsAsWindows1250()
    {
        var data = DataWith("Nováková, Jiří");

        var result = _formatter.Format(data, Config());
        var windows1250 = Encoding.GetEncoding(PayrollExportConstants.Windows1250CodePage);
        var utf8Text = Encoding.UTF8.GetString(result.Content);
        var windows1250Text = windows1250.GetString(result.Content);

        windows1250Text.ShouldContain("Nováková");
        windows1250Text.ShouldContain("Jiří");
        utf8Text.ShouldNotContain("Nováková");
    }

    [Test]
    public void Format_ExposesFormatKeyContentTypeAndExtension()
    {
        _formatter.FormatKey.ShouldBe(PayrollExportConstants.FormatKeyPohodaCz);
        _formatter.ContentType.ShouldBe(PayrollExportConstants.ContentTypeXml);
        _formatter.FileExtension.ShouldBe(PayrollExportConstants.FileExtensionXml);
    }
}
