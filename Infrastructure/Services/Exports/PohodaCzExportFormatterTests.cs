// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the Stormware PAMICA/POHODA dochazka_zamestnance formatter: ZIP-of-one-document-
/// per-employee packaging, root element and version attribute, cislo_pracovniho_pomeru matching key,
/// presence-vs-absence section assignment, both absence mapping shapes (Kod and the common WageType), absence hours in
/// rozvrh, unmapped and empty-mapped absence skipping and Windows-1250 encoding.
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
    private static readonly XName Jmeno = Ns + "jmeno";
    private static readonly XName Prijmeni = Ns + "prijmeni";
    private static readonly XName Nepritomnosti = Ns + "nepritomnosti";
    private static readonly XName Nepritomnost = Ns + "nepritomnost";
    private static readonly XName Pritomnost = Ns + "pritomnost";
    private static readonly XName Rozvrh = Ns + "rozvrh";
    private static readonly XName Uvazek = Ns + "uvazek";
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
            FirstName = fullName.Contains(", ") ? fullName.Split(", ", 2)[1] : string.Empty,
            LastName = fullName.Contains(", ") ? fullName.Split(", ", 2)[0] : fullName,
            Entries = entries.ToList(),
        });
    }

    private static PayrollExportData DataWithEmployees(params PayrollEmployee[] employees)
    {
        return new PayrollExportData
        {
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
    public void Format_WritesJmenoAndPrijmeni_FromStructuredNameFields()
    {
        var data = DataWith("Muster-Meier, Max Moritz", new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.WorkHours,
            Quantity = 8m,
        });

        var document = ParseSingleEntry(_formatter.Format(data, Config()).Content);

        document.Root!.Element(Hlavicka)!.Element(Jmeno)!.Value.ShouldBe("Max Moritz");
        document.Root!.Element(Hlavicka)!.Element(Prijmeni)!.Value.ShouldBe("Muster-Meier");
    }

    [Test]
    public void Format_WritesLastNameOnly_WhenEmployeeHasNoFirstName()
    {
        var data = DataWith("Muster", new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 15),
            Kind = PayrollEntryKind.WorkHours,
            Quantity = 8m,
        });

        var document = ParseSingleEntry(_formatter.Format(data, Config()).Content);

        document.Root!.Element(Hlavicka)!.Element(Prijmeni)!.Value.ShouldBe("Muster");
        document.Root!.Element(Hlavicka)!.Element(Jmeno).ShouldBeNull();
    }

    [Test]
    public void Format_WorkHours_AreWrittenUnderRozvrhUvazekAndNotUnderPritomnostOrNepritomnosti()
    {
        var data = DataWith(
            "Muster, Max",
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 15), Kind = PayrollEntryKind.WorkHours, Quantity = 8.5m },
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 16), Kind = PayrollEntryKind.WorkHours, Quantity = 4m });

        var result = _formatter.Format(data, Config());
        var document = ParseSingleEntry(result.Content);

        document.Root!.Element(Pritomnost)!.Elements().ShouldBeEmpty();
        var uvazekByDate = document.Root.Element(Rozvrh)!.Elements(Uvazek)
            .ToDictionary(e => e.Attribute("datum")!.Value, e => e.Value);
        uvazekByDate["2026-01-15"].ShouldBe("8:30");
        uvazekByDate["2026-01-16"].ShouldBe("4:00");
        uvazekByDate["2026-01-17"].ShouldBe("0:00");
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
    public void Format_CommonMappingShape_UsesWageTypeAsKod_AndKodWinsWhenBothAreSet()
    {
        var commonId = Guid.NewGuid();
        var bothId = Guid.NewGuid();
        var mapping = $"{{\"{commonId}\":{{\"WageType\":\"N01\",\"Ausfallschluessel\":\"K\"}},"
            + $"\"{bothId}\":{{\"WageType\":\"N01\",\"Kod\":\"N05\"}}}}";

        var data = DataWith(
            "Muster, Max",
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 20), Kind = PayrollEntryKind.Absence, Quantity = 8m, AbsenceId = commonId },
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 21), Kind = PayrollEntryKind.Absence, Quantity = 8m, AbsenceId = bothId });

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));
        var document = ParseSingleEntry(result.Content);

        document.Root!.Element(Nepritomnosti)!.Elements(Nepritomnost)
            .Select(e => e.Element(Kod)!.Value).ToList()
            .ShouldBe(new List<string> { "N01", "N05" });
        result.RecordCount.ShouldBe(2);
        result.SkippedAbsenceCount.ShouldBe(0);
    }

    [TestCase("{{\"{0}\":{{\"WageType\":\"\"}}}}")]
    [TestCase("{{\"{0}\":{{\"Kod\":\" \",\"WageType\":\"  \"}}}}")]
    [TestCase("{{\"{0}\":{{\"Ausfallschluessel\":\"K\"}}}}")]
    public void Format_AbsenceMappedToEmptyKod_IsSkippedAndCounted_AndAddsNoHoursToRozvrh(string mappingTemplate)
    {
        var absenceId = Guid.NewGuid();
        var mapping = string.Format(System.Globalization.CultureInfo.InvariantCulture, mappingTemplate, absenceId);

        var data = DataWith("Muster, Max", new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 20),
            Kind = PayrollEntryKind.Absence,
            Quantity = 8m,
            AbsenceId = absenceId,
        });

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));
        var document = ParseSingleEntry(result.Content);

        document.Root!.Element(Nepritomnosti)!.Elements(Nepritomnost).ShouldBeEmpty();
        UvazekByDate(document)["2026-01-20"].ShouldBe("0:00");
        result.RecordCount.ShouldBe(0);
        result.SkippedAbsenceCount.ShouldBe(1);
    }

    [Test]
    public void Format_MappedAbsenceHours_AreAddedToThatDaysUvazek()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":{{\"WageType\":\"N01\"}}}}";

        var data = DataWith(
            "Muster, Max",
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 15), Kind = PayrollEntryKind.WorkHours, Quantity = 4.5m },
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 15), Kind = PayrollEntryKind.Absence, Quantity = 3.75m, AbsenceId = absenceId },
            new PayrollDayEntry { Date = new DateOnly(2026, 1, 20), Kind = PayrollEntryKind.Absence, Quantity = 8m, AbsenceId = absenceId },
            new PayrollDayEntry
            {
                Date = new DateOnly(2026, 1, 22),
                Kind = PayrollEntryKind.Absence,
                Quantity = 1m,
                Unit = PayrollQuantityUnit.Days,
                AbsenceId = absenceId,
            });

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));
        var uvazekByDate = UvazekByDate(ParseSingleEntry(result.Content));

        uvazekByDate["2026-01-15"].ShouldBe("8:15");
        uvazekByDate["2026-01-20"].ShouldBe("8:00");
        uvazekByDate["2026-01-22"].ShouldBe("0:00");
        uvazekByDate["2026-01-21"].ShouldBe("0:00");
        uvazekByDate.Count.ShouldBe(31);
        result.RecordCount.ShouldBe(4);
    }

    [Test]
    public void Format_MappedAbsence_WritesExactNepritomnostElement()
    {
        var absenceId = Guid.NewGuid();
        var mapping = $"{{\"{absenceId}\":{{\"WageType\":\"N01\",\"Ausfallschluessel\":\"K\"}}}}";

        var data = DataWith("Muster, Max", new PayrollDayEntry
        {
            Date = new DateOnly(2026, 1, 20),
            Kind = PayrollEntryKind.Absence,
            Quantity = 8m,
            AbsenceId = absenceId,
        });

        var result = _formatter.Format(data, Config(absenceMappingJson: mapping));
        var text = Encoding.GetEncoding(PayrollExportConstants.Windows1250CodePage)
            .GetString(ExtractZipEntries(result.Content)[0].Content);

        text.ShouldContain(
            "    <uvazek datum=\"2026-01-20\">8:00</uvazek>\r\n");
        text.ShouldContain(
            "  <nepritomnosti>\r\n"
            + "    <nepritomnost>\r\n"
            + "      <kod>N01</kod>\r\n"
            + "      <od>2026-01-20</od>\r\n"
            + "      <do>2026-01-20</do>\r\n"
            + "    </nepritomnost>\r\n"
            + "  </nepritomnosti>\r\n");
    }

    private static Dictionary<string, string> UvazekByDate(XDocument document)
    {
        return document.Root!.Element(Rozvrh)!.Elements(Uvazek)
            .ToDictionary(e => e.Attribute("datum")!.Value, e => e.Value);
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
                FirstName = "A",
                LastName = "A",
                Entries = [new PayrollDayEntry { Date = new DateOnly(2026, 1, 15), Kind = PayrollEntryKind.WorkHours, Quantity = 8m }],
            },
            new PayrollEmployee
            {
                ClientId = Guid.NewGuid(),
                IdNumber = 2,
                FullName = "B, B",
                FirstName = "B",
                LastName = "B",
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
