// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the Stormware PAMICA/POHODA dochazka_zamestnance formatter: ZIP-of-one-document-
/// per-employee packaging, root element and version attribute, cislo_pracovniho_pomeru matching key,
/// presence-vs-absence section assignment, unmapped-absence skipping and Windows-1250 encoding.
/// </summary>
using System.IO.Compression;
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
    private static readonly XNamespace Ns = "http://www.stormware.cz/schema/pamica/version_2/dochazka.xsd";
    private const string DochazkaZamestnance = "dochazka_zamestnance";
    private static readonly XName Hlavicka = Ns + "hlavicka";
    private static readonly XName CisloPracovnihoPomeru = Ns + "cislo_pracovniho_pomeru";
    private static readonly XName Nepritomnosti = Ns + "nepritomnosti";
    private static readonly XName Nepritomnost = Ns + "nepritomnost";
    private static readonly XName Pritomnost = Ns + "pritomnost";
    private static readonly XName PrescasPracovniDen = Ns + "prescas_pracovni_den";
    private static readonly XName Hodiny = Ns + "hodiny";
    private static readonly XName Kod = Ns + "kod";
    private static readonly XName Od = Ns + "od";
    private static readonly XName Do = Ns + "do";
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
        return DataWithEmployees(new PayrollEmployee
        {
            ClientId = Guid.NewGuid(),
            IdNumber = 42,
            FullName = fullName,
            Entries = entries.ToList(),
        });
    }

    private static PayrollExportData DataWithEmployees(params PayrollEmployee[] employees)
    {
        return new PayrollExportData
        {
            GroupId = Guid.NewGuid(),
            StartDate = new DateOnly(2026, 1, 1),
            EndDate = new DateOnly(2026, 1, 31),
            Employees = employees.ToList(),
        };
    }

    private static List<(string EntryName, byte[] Content)> ExtractZipEntries(byte[] zipBytes)
    {
        using var stream = new MemoryStream(zipBytes);
        using var archive = new ZipArchive(stream, ZipArchiveMode.Read);

        var entries = new List<(string, byte[])>();
        foreach (var entry in archive.Entries)
        {
            using var entryStream = entry.Open();
            using var buffer = new MemoryStream();
            entryStream.CopyTo(buffer);
            entries.Add((entry.Name, buffer.ToArray()));
        }

        return entries;
    }

    private static XDocument ParseSingleEntry(byte[] zipBytes)
    {
        var entries = ExtractZipEntries(zipBytes);
        entries.Count.ShouldBe(1);
        var text = Encoding.GetEncoding(PayrollExportConstants.Windows1250CodePage).GetString(entries[0].Content);
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
        var document = ParseSingleEntry(result.Content);

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
        var document = ParseSingleEntry(result.Content);

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
        var document = ParseSingleEntry(result.Content);

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
        var document = ParseSingleEntry(result.Content);

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
        var document = ParseSingleEntry(result.Content);

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
        var document = ParseSingleEntry(result.Content);

        var priplatek = document.Root!.Element(Ns + "mzdy")!.Element(Ns + "priplatek")!;
        priplatek.Element(Kod)!.Value.ShouldBe("P07");
        priplatek.Element(Hodiny)!.Value.ShouldBe("2:15");
        result.RecordCount.ShouldBe(1);
    }

    [Test]
    public void Format_MultipleEmployees_ProducesOneZipEntryPerEmployeeWithSummedCounts()
    {
        var data = DataWithEmployees(
            new PayrollEmployee
            {
                ClientId = Guid.NewGuid(),
                IdNumber = 1,
                FullName = "A, A",
                Entries = [new PayrollDayEntry { Date = new DateOnly(2026, 1, 15), Kind = PayrollEntryKind.WorkHours, Quantity = 8m }],
            },
            new PayrollEmployee
            {
                ClientId = Guid.NewGuid(),
                IdNumber = 2,
                FullName = "B, B",
                Entries = [new PayrollDayEntry { Date = new DateOnly(2026, 1, 15), Kind = PayrollEntryKind.WorkHours, Quantity = 6m }],
            });

        var result = _formatter.Format(data, Config());
        var entries = ExtractZipEntries(result.Content);

        entries.Count.ShouldBe(2);
        entries.Select(e => e.EntryName).ShouldBe(["dochazka_1.xml", "dochazka_2.xml"], ignoreOrder: true);

        var windows1250 = Encoding.GetEncoding(PayrollExportConstants.Windows1250CodePage);
        var employeeOne = XDocument.Parse(windows1250.GetString(entries.Single(e => e.EntryName == "dochazka_1.xml").Content));
        employeeOne.Root!.Element(Hlavicka)!.Element(CisloPracovnihoPomeru)!.Value.ShouldBe("1");

        result.RecordCount.ShouldBe(2);
        result.SkippedAbsenceCount.ShouldBe(0);
    }

    [Test]
    public void Format_EncodesCzechDiacriticsAsWindows1250()
    {
        var data = DataWith("Nováková, Jiří");

        var result = _formatter.Format(data, Config());
        var entries = ExtractZipEntries(result.Content);
        var windows1250 = Encoding.GetEncoding(PayrollExportConstants.Windows1250CodePage);
        var windows1250Text = windows1250.GetString(entries[0].Content);
        var utf8Text = Encoding.UTF8.GetString(entries[0].Content);

        windows1250Text.ShouldContain("Nováková");
        windows1250Text.ShouldContain("Jiří");
        utf8Text.ShouldNotContain("Nováková");
    }

    [Test]
    public void Format_ExposesFormatKeyContentTypeAndExtension()
    {
        _formatter.FormatKey.ShouldBe(PayrollExportConstants.FormatKeyPohodaCz);
        _formatter.ContentType.ShouldBe(PayrollExportConstants.ContentTypeZip);
        _formatter.FileExtension.ShouldBe(PayrollExportConstants.FileExtensionZip);
    }
}
