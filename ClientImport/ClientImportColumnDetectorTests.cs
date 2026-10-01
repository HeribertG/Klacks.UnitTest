// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Column detection of the employee import: German/English/French/Italian headers, a generic "Name"
/// next to "Vorname" read as last name, contained-synonym headers, content-based detection of
/// headerless columns and one column per target.
/// </summary>

using Klacks.Api.Application.Services.ClientImport;
using Klacks.Api.Domain.Enums;

namespace Klacks.UnitTest.ClientImport;

[TestFixture]
public class ClientImportColumnDetectorTests
{
    private const int CurrentYear = 2026;

    private ClientImportColumnDetector _detector = null!;

    [OneTimeSetUp]
    public void Setup()
    {
        _detector = new ClientImportColumnDetector(ClientImportSynonymCatalog.LoadEmbedded());
    }

    [Test]
    public void GermanHeaders_AreRecognised()
    {
        var targets = Detect(["Anrede", "Name", "Vorname", "Strasse", "PLZ", "Ort", "Geburtsdatum", "Eintritt", "Natel", "E-Mail", "Personal-Nr."]);

        targets.ShouldBe([
            ClientImportTarget.Salutation, ClientImportTarget.LastName, ClientImportTarget.FirstName, ClientImportTarget.Street,
            ClientImportTarget.Zip, ClientImportTarget.City, ClientImportTarget.Birthdate, ClientImportTarget.EntryDate,
            ClientImportTarget.Mobile, ClientImportTarget.Email, ClientImportTarget.PersonnelNumber]);
    }

    [Test]
    public void EnglishHeaders_AreRecognised()
    {
        Detect(["First Name", "Last Name", "Date of Birth", "ZIP Code", "City", "Mobile Phone", "Hire Date", "Department"])
            .ShouldBe([
                ClientImportTarget.FirstName, ClientImportTarget.LastName, ClientImportTarget.Birthdate, ClientImportTarget.Zip,
                ClientImportTarget.City, ClientImportTarget.Mobile, ClientImportTarget.EntryDate, ClientImportTarget.Group]);
    }

    [Test]
    public void FrenchHeaders_AreRecognised()
    {
        Detect(["Civilité", "Nom", "Prénom", "NPA", "Localité", "Date de naissance", "Date d'entrée", "Courriel"])
            .ShouldBe([
                ClientImportTarget.Salutation, ClientImportTarget.LastName, ClientImportTarget.FirstName, ClientImportTarget.Zip,
                ClientImportTarget.City, ClientImportTarget.Birthdate, ClientImportTarget.EntryDate, ClientImportTarget.Email]);
    }

    [Test]
    public void ItalianHeaders_AreRecognised()
    {
        Detect(["Cognome", "Nome", "Via", "CAP", "Località", "Data di nascita", "Cellulare"])
            .ShouldBe([
                ClientImportTarget.LastName, ClientImportTarget.FirstName, ClientImportTarget.Street, ClientImportTarget.Zip,
                ClientImportTarget.City, ClientImportTarget.Birthdate, ClientImportTarget.Mobile]);
    }

    [Test]
    public void GenericName_WithoutFirstNameColumn_IsTheFullName()
    {
        Detect(["Name", "Ort"]).ShouldBe([ClientImportTarget.FullName, ClientImportTarget.City]);
    }

    [Test]
    public void ContainedSynonym_IsAWeakerMatch()
    {
        var mapping = _detector.Detect(["E-Mail geschäftlich"], [], CurrentYear);

        mapping[0].Target.ShouldBe(ClientImportTarget.Email);
        mapping[0].Confidence.ShouldBe(ClientImportColumnDetector.ContainedHeaderConfidence);
    }

    [Test]
    public void HeaderlessColumns_AreDetectedByContent()
    {
        IReadOnlyList<IReadOnlyList<string>> rows =
        [
            ["anna@example.com", "Frau", "8000 Zürich", "05.03.1990", "+41 79 123 45 67", "xyz"],
            ["ben@example.org", "Herr", "3000 Bern", "12.11.1985", "079 765 43 21", "abc"]
        ];

        var mapping = _detector.Detect(["Spalte1", "Spalte2", "Spalte3", "Spalte4", "Spalte5", "Spalte6"], rows, CurrentYear);

        mapping.Select(m => m.Target).ShouldBe([
            ClientImportTarget.Email, ClientImportTarget.Gender, ClientImportTarget.ZipCity, ClientImportTarget.Birthdate,
            ClientImportTarget.Phone, ClientImportTarget.Ignore]);
    }

    [Test]
    public void RecentDates_WithoutHeader_AreEntryDates()
    {
        IReadOnlyList<IReadOnlyList<string>> rows = [["01.02.2024"], ["15.08.2025"]];

        _detector.Detect(["x"], rows, CurrentYear)[0].Target.ShouldBe(ClientImportTarget.EntryDate);
    }

    [Test]
    public void EveryTarget_IsGivenToOneColumnOnly()
    {
        var mapping = _detector.Detect(["E-Mail", "Email address", "Telefon"], [], CurrentYear);

        mapping.Count(m => m.Target == ClientImportTarget.Email).ShouldBe(1);
        mapping[0].Target.ShouldBe(ClientImportTarget.Email);
        mapping[1].Target.ShouldBe(ClientImportTarget.Ignore);
        mapping[1].Confidence.ShouldBe(0);
    }

    [TestCase("Name, Vorname")]
    [TestCase("Name Vorname")]
    [TestCase("Vorname Name")]
    [TestCase("Nachname, Vorname")]
    [TestCase("Last name, first name")]
    [TestCase("Nom, Prénom")]
    [TestCase("Cognome e nome")]
    [TestCase("Familienname / Rufname")]
    [TestCase("Surname and given name")]
    public void CombinedNameHeader_IsTheFullName(string header)
    {
        Detect(["Anrede", header, "Geburtsdatum"]).ShouldBe([ClientImportTarget.Salutation, ClientImportTarget.FullName, ClientImportTarget.Birthdate]);
    }

    [Test]
    public void CombinedNameHeaderOutsideTheVocabulary_IsTheFullName()
    {
        var catalog = ClientImportSynonymCatalog.LoadEmbedded();
        catalog.MatchHeader("Familienname / Rufname").ShouldBeNull();
        catalog.MatchHeader("Surname and given name").ShouldBeNull();

        Detect(["Familienname / Rufname"]).ShouldBe([ClientImportTarget.FullName]);
        Detect(["Surname and given name"]).ShouldBe([ClientImportTarget.FullName]);
    }

    [TestCase("Employee First Name", ClientImportTarget.FirstName)]
    [TestCase("Employee Last Name", ClientImportTarget.LastName)]
    [TestCase("Phone extension", ClientImportTarget.Phone)]
    public void HeaderWithOnlyOneNamePart_IsNotACombinedName(string header, ClientImportTarget expected)
    {
        Detect([header]).ShouldBe([expected]);
    }

    [TestCase("Tenure hours")]
    [TestCase("Hometown")]
    [TestCase("名前の読み")]
    public void ShortNameTokens_DoNotMakeAHeaderACombinedName(string header)
    {
        Detect([header])[0].ShouldNotBe(ClientImportTarget.FullName);
    }

    [Test]
    public void CombinedNameColumn_NextToAFirstNameColumn_StaysTheFullName()
    {
        Detect(["Name, Vorname", "Rufname"]).ShouldBe([ClientImportTarget.FullName, ClientImportTarget.FirstName]);
    }

    [TestCase("Name, Vorname", ClientImportNameOrder.LastFirst)]
    [TestCase("Name Vorname", ClientImportNameOrder.LastFirst)]
    [TestCase("Vorname Name", ClientImportNameOrder.FirstLast)]
    [TestCase("Nachname, Vorname", ClientImportNameOrder.LastFirst)]
    [TestCase("Vorname Nachname", ClientImportNameOrder.FirstLast)]
    [TestCase("Last name, first name", ClientImportNameOrder.LastFirst)]
    [TestCase("Surname and given name", ClientImportNameOrder.LastFirst)]
    [TestCase("First name last name", ClientImportNameOrder.FirstLast)]
    [TestCase("Nom, Prénom", ClientImportNameOrder.LastFirst)]
    [TestCase("Nom et prénom", ClientImportNameOrder.LastFirst)]
    [TestCase("Cognome e nome", ClientImportNameOrder.LastFirst)]
    [TestCase("Nome e cognome", ClientImportNameOrder.FirstLast)]
    [TestCase("Nume și prenume", ClientImportNameOrder.LastFirst)]
    [TestCase("Imię i nazwisko", ClientImportNameOrder.FirstLast)]
    [TestCase("Celé jméno", ClientImportNameOrder.FirstLast)]
    [TestCase("Nombre completo", ClientImportNameOrder.FirstLast)]
    [TestCase("Name", ClientImportNameOrder.FirstLast)]
    [TestCase("Họ và tên", ClientImportNameOrder.LastFirst)]
    [TestCase("姓名", ClientImportNameOrder.LastFirst)]
    public void NameOrder_FollowsTheOrderOfTheNamePartsInTheHeader(string header, ClientImportNameOrder expected)
    {
        IReadOnlyList<IReadOnlyList<string>> rows = [["Anna Müller"], ["Ben Meier"]];
        var mapping = _detector.Detect([header], rows, CurrentYear);

        mapping[0].Target.ShouldBe(ClientImportTarget.FullName);
        _detector.DetectNameOrder([header], mapping, rows).ShouldBe(expected);
    }

    [Test]
    public void NameOrder_CommasInMostValues_WinOverTheHeader()
    {
        IReadOnlyList<IReadOnlyList<string>> rows = [["Müller, Anna"], ["Meier, Ben"], ["Gerber, Jürg"], ["Keller, Eva"], ["Ueli Frei"]];
        var mapping = _detector.Detect(["Vorname Name"], rows, CurrentYear);

        _detector.DetectNameOrder(["Vorname Name"], mapping, rows).ShouldBe(ClientImportNameOrder.LastFirst);
    }

    [Test]
    public void NameOrder_WithoutFullNameColumn_IsFirstLast()
    {
        IReadOnlyList<IReadOnlyList<string>> rows = [["Müller", "Anna"]];
        var mapping = _detector.Detect(["Nachname", "Vorname"], rows, CurrentYear);

        _detector.DetectNameOrder(["Nachname", "Vorname"], mapping, rows).ShouldBe(ClientImportNameOrder.FirstLast);
    }

    private List<ClientImportTarget> Detect(string[] headers) =>
        _detector.Detect(headers, [], CurrentYear).Select(m => m.Target).ToList();
}
