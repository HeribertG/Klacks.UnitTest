// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Value parsing of the employee import: dates in the confirmed order (never guessed) with UTC kind and
/// the two-digit-year pivot, the day/month order detection, full-name splitting, "postcode city"
/// splitting, street + house number and the phone split shared with create_employee.
/// </summary>

using Klacks.Api.Application.Common;
using Klacks.Api.Application.Services.ClientImport;
using Klacks.Api.Domain.Enums;

namespace Klacks.UnitTest.ClientImport;

[TestFixture]
public class ClientImportValueParserTests
{
    private const int CurrentYear = 2026;

    [TestCase("05.03.1990", ClientImportDateFormat.DayMonthYear, 1990, 3, 5)]
    [TestCase("05/03/1990", ClientImportDateFormat.MonthDayYear, 1990, 5, 3)]
    [TestCase("1990/03/05", ClientImportDateFormat.DayMonthYear, 1990, 3, 5)]
    [TestCase("1990-03-05", ClientImportDateFormat.MonthDayYear, 1990, 3, 5)]
    [TestCase("90.03.05", ClientImportDateFormat.YearMonthDay, 1990, 3, 5)]
    [TestCase("5.3.1990 00:00", ClientImportDateFormat.DayMonthYear, 1990, 3, 5)]
    [TestCase("2024-01-31T08:15:00", ClientImportDateFormat.DayMonthYear, 2024, 1, 31)]
    public void Date_IsReadInTheChosenOrder(string value, ClientImportDateFormat format, int year, int month, int day)
    {
        ClientImportDateParser.TryParse(value, format, CurrentYear, out var date, out _).ShouldBeTrue();

        date.ShouldBe(new DateTime(year, month, day, 0, 0, 0, DateTimeKind.Utc));
        date.Kind.ShouldBe(DateTimeKind.Utc);
    }

    [TestCase("31.02.2020")]
    [TestCase("13/13/2020")]
    [TestCase("gestern")]
    [TestCase("")]
    public void InvalidDate_IsRejected(string value)
    {
        ClientImportDateParser.TryParse(value, ClientImportDateFormat.DayMonthYear, CurrentYear, out _, out _).ShouldBeFalse();
    }

    [TestCase("01.02.26", 2026)]
    [TestCase("01.02.27", 1927)]
    [TestCase("01.02.85", 1985)]
    [TestCase("01.02.00", 2000)]
    public void TwoDigitYear_UsesThePivotAndIsReported(string value, int expectedYear)
    {
        ClientImportDateParser.TryParse(value, ClientImportDateFormat.DayMonthYear, CurrentYear, out var date, out var twoDigit).ShouldBeTrue();

        date.Year.ShouldBe(expectedYear);
        twoDigit.ShouldBeTrue();
    }

    [Test]
    public void DateOrder_DayAbove12_ProvesDayFirst()
    {
        ClientImportDateFormatDetector.Detect(["01.02.1990", "25.12.1985"]).ShouldBe((ClientImportDateFormat.DayMonthYear, false));
    }

    [Test]
    public void DateOrder_SecondAbove12_ProvesMonthFirst()
    {
        ClientImportDateFormatDetector.Detect(["01/02/1990", "12/25/1985"]).ShouldBe((ClientImportDateFormat.MonthDayYear, false));
    }

    [Test]
    public void DateOrder_WithoutEvidence_IsAmbiguous()
    {
        ClientImportDateFormatDetector.Detect(["01.02.1990", "03.04.1985"]).Ambiguous.ShouldBeTrue();
    }

    [Test]
    public void DateOrder_ContradictingEvidence_IsAmbiguous()
    {
        ClientImportDateFormatDetector.Detect(["25.02.1990", "03.14.1985"]).Ambiguous.ShouldBeTrue();
    }

    [Test]
    public void DateOrder_OnlyIsoDates_IsNotAmbiguous()
    {
        ClientImportDateFormatDetector.Detect(["1990-02-01", "1985-04-03"]).Ambiguous.ShouldBeFalse();
    }

    [TestCase("Hans Müller", ClientImportNameOrder.FirstLast, "Hans", "Müller", false)]
    [TestCase("Müller Hans", ClientImportNameOrder.LastFirst, "Hans", "Müller", false)]
    [TestCase("Müller, Hans Peter", ClientImportNameOrder.FirstLast, "Hans Peter", "Müller", false)]
    [TestCase("Hans Peter Müller", ClientImportNameOrder.FirstLast, "Hans Peter", "Müller", true)]
    [TestCase("Anna van der Berg", ClientImportNameOrder.FirstLast, "Anna", "van der Berg", true)]
    [TestCase("Anna Van Der Berg", ClientImportNameOrder.FirstLast, "Anna", "Van Der Berg", true)]
    [TestCase("Maria De Souza", ClientImportNameOrder.FirstLast, "Maria", "De Souza", true)]
    [TestCase("Müller", ClientImportNameOrder.FirstLast, null, "Müller", false)]
    public void FullName_IsSplit(string value, ClientImportNameOrder order, string? first, string? last, bool multiWord)
    {
        ClientImportNameSplitter.Split(value, order).ShouldBe(new ClientImportNameParts(first, last, multiWord));
    }

    [TestCase("8000 Zürich", "8000", "Zürich")]
    [TestCase("D-80331 München", "80331", "München")]
    [TestCase("111 22 Stockholm", "111 22", "Stockholm")]
    [TestCase("00-950 Warszawa", "00-950", "Warszawa")]
    [TestCase("1204 Genève 4", "1204", "Genève 4")]
    public void ZipCity_IsSplit(string value, string zip, string city)
    {
        ClientImportAddressSplitter.TrySplitZipCity(value, out var actualZip, out var actualCity).ShouldBeTrue();

        actualZip.ShouldBe(zip);
        actualCity.ShouldBe(city);
    }

    [TestCase("Zürich")]
    [TestCase("London SW1A 1AA")]
    public void ZipCity_WithoutLeadingPostcode_IsNotSplit(string value)
    {
        ClientImportAddressSplitter.TrySplitZipCity(value, out _, out _).ShouldBeFalse();
    }

    [TestCase("Bahnhofstrasse", "12a", "Bahnhofstrasse 12a")]
    [TestCase("Bahnhofstrasse 12", null, "Bahnhofstrasse 12")]
    [TestCase(null, null, null)]
    public void Street_AndHouseNumber_AreJoined(string? street, string? number, string? expected)
    {
        ClientImportAddressSplitter.CombineStreet(street, number).ShouldBe(expected);
    }

    [TestCase("079 123 45 67", "+41", "+41", "791234567")]
    [TestCase("+41 79 123 45 67", "+41", "+41", "791234567")]
    [TestCase("0041 79 123 45 67", "+41", "+41", "791234567")]
    [TestCase("+49 170 1234567", "+41", "", "+491701234567")]
    [TestCase("079 123 45 67", "", "", "0791234567")]
    [TestCase("", "+41", "", "")]
    public void Phone_IsSplitIntoPrefixAndNumber(string phone, string countryPrefix, string prefix, string number)
    {
        PhoneNumberSplitter.Split(phone, countryPrefix).ShouldBe((prefix, number));
    }
}
