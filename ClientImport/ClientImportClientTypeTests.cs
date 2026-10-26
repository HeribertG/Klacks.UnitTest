// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Client type and company in the employee import: type words of all core languages and the numeric
/// codes, empty type as employee, unknown type as error, the legal entity (customer with company; a person
/// name is kept as contact person, the gender cell ignored) with its address rule, a customer without company
/// as person, unchanged name rules for employees and external employees, no
/// contract for customers, the written client, the header detection of "Typ"/"Firma" (owner's test files)
/// and the duplicate search within customers.
/// </summary>

using Klacks.Api.Application.Commands.ClientImport;
using Klacks.Api.Application.DTOs.ClientImport;
using Klacks.Api.Application.Handlers.ClientImport;
using Klacks.Api.Application.Interfaces.ClientImport;
using Klacks.Api.Application.Services.ClientImport;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Email;
using Microsoft.Extensions.Logging.Abstractions;
using T = Klacks.Api.Domain.Enums.ClientImportTarget;

namespace Klacks.UnitTest.ClientImport;

[TestFixture]
public class ClientImportClientTypeTests
{
    private const int CurrentYear = 2026;

    private static readonly T[] Person = [T.FirstName, T.LastName, T.Gender, T.Type, T.Company];
    private static readonly T[] Company = [T.FirstName, T.LastName, T.Type, T.Company, T.Street, T.Zip, T.City];

    private ClientImportSynonymCatalog _catalog = null!;

    [OneTimeSetUp]
    public void LoadCatalog()
    {
        _catalog = ClientImportSynonymCatalog.LoadEmbedded();
    }

    [TestCase("Mitarbeiter", EntityTypeEnum.Employee)]
    [TestCase("Mitarbeiterin", EntityTypeEnum.Employee)]
    [TestCase("Angestellter", EntityTypeEnum.Employee)]
    [TestCase("Employee", EntityTypeEnum.Employee)]
    [TestCase("Collaborateur", EntityTypeEnum.Employee)]
    [TestCase("Employé", EntityTypeEnum.Employee)]
    [TestCase("Dipendente", EntityTypeEnum.Employee)]
    [TestCase("Collaboratore", EntityTypeEnum.Employee)]
    [TestCase("0", EntityTypeEnum.Employee)]
    [TestCase("Extern", EntityTypeEnum.ExternEmp)]
    [TestCase("Externer", EntityTypeEnum.ExternEmp)]
    [TestCase("Externer Mitarbeiter", EntityTypeEnum.ExternEmp)]
    [TestCase("External", EntityTypeEnum.ExternEmp)]
    [TestCase("Externe", EntityTypeEnum.ExternEmp)]
    [TestCase("Collaborateur externe", EntityTypeEnum.ExternEmp)]
    [TestCase("Esterno", EntityTypeEnum.ExternEmp)]
    [TestCase("Collaboratore esterno", EntityTypeEnum.ExternEmp)]
    [TestCase("1", EntityTypeEnum.ExternEmp)]
    [TestCase("Kunde", EntityTypeEnum.Customer)]
    [TestCase("Customer", EntityTypeEnum.Customer)]
    [TestCase("Client", EntityTypeEnum.Customer)]
    [TestCase("Cliente", EntityTypeEnum.Customer)]
    [TestCase("2", EntityTypeEnum.Customer)]
    [TestCase("  KUNDE ", EntityTypeEnum.Customer)]
    [TestCase("mitarbeiter", EntityTypeEnum.Employee)]
    public void ResolveType_ReadsTheTypeWordsAndCodes(string value, EntityTypeEnum expected)
    {
        _catalog.ResolveType(value).ShouldBe(expected);
    }

    [TestCase("Lieferant")]
    [TestCase("3")]
    [TestCase("-1")]
    [TestCase("Mitarbeiter extern")]
    [TestCase("")]
    [TestCase(null)]
    public void ResolveType_UnknownOrEmpty_IsNull(string? value)
    {
        _catalog.ResolveType(value).ShouldBeNull();
    }

    [TestCaseSource(typeof(ClientImportSynonymCatalogTests), nameof(ClientImportSynonymCatalogTests.SupportedLanguages))]
    public void EveryLanguage_NamesEveryClientType(string language)
    {
        var types = _catalog.TypeValues.First(t => string.Equals(t.Key, language, StringComparison.OrdinalIgnoreCase)).Value;

        foreach (var type in Enum.GetValues<EntityTypeEnum>())
        {
            types.ShouldContainKey(type, $"{language}: no words for {type}");
            types[type].ShouldNotBeEmpty();
        }
    }

    [Test]
    public void NoNormalizedTypeWord_MapsToTwoTypes()
    {
        var collisions = _catalog.TypeValues
            .SelectMany(language => language.Value.SelectMany(type => type.Value.Select(word =>
                (Language: language.Key, Type: type.Key, Key: ClientImportTextNormalizer.Normalize(word)))))
            .GroupBy(entry => entry.Key)
            .Where(group => group.Select(e => e.Type).Distinct().Count() > 1)
            .Select(group => $"'{group.Key}': " + string.Join(", ", group.Select(e => $"{e.Language}/{e.Type}")))
            .ToList();

        collisions.ShouldBeEmpty();
    }

    [Test]
    public void TypeExample_ListsTheFirstWordOfEveryType()
    {
        _catalog.TypeExample("de").ShouldBe("Mitarbeiter / Extern / Kunde");
        _catalog.TypeExample("EN").ShouldBe("Employee / External / Customer");
        _catalog.TypeExample("xx").ShouldBeNull();
    }

    [Test]
    public void OwnerTestFileHeaders_AreMappedCompletely()
    {
        var headers = "Vorname;Nachname;Titel;Typ;Firma;Geschlecht;Geburtsdatum;Strasse;PLZ;Ort;Kanton;Land;E-Mail;Telefon;Mobil;Eintrittsdatum;Austrittsdatum;Vertrag;Bemerkung"
            .Split(';');

        var mapping = new ClientImportColumnDetector(_catalog).Detect(headers, [], CurrentYear);

        mapping.Select(m => m.Target).ShouldBe([
            T.FirstName, T.LastName, T.Title, T.Type, T.Company, T.Gender, T.Birthdate, T.Street, T.Zip, T.City, T.State, T.Country,
            T.Email, T.Phone, T.Mobile, T.EntryDate, T.ExitDate, T.Contract, T.Note]);
        mapping.ShouldAllBe(m => m.Confidence == ClientImportColumnDetector.ExactHeaderConfidence);
    }

    [TestCase("Art", T.Type)]
    [TestCase("Mitarbeitertyp", T.Type)]
    [TestCase("Type", T.Type)]
    [TestCase("Tipo", T.Type)]
    [TestCase("Unternehmen", T.Company)]
    [TestCase("Company", T.Company)]
    [TestCase("Entreprise", T.Company)]
    [TestCase("Société", T.Company)]
    [TestCase("Azienda", T.Company)]
    [TestCase("Ditta", T.Company)]
    public void TypeAndCompanyHeaders_AreRecognised(string header, T expected)
    {
        new ClientImportColumnDetector(_catalog).Detect([header], [], CurrentYear).Single().Target.ShouldBe(expected);
    }

    [TestCase("Company phone", T.Phone)]
    [TestCase("E-Mail Firma", T.Email)]
    [TestCase("Telefon Firma", T.Phone)]
    [TestCase("Firmenadresse Strasse", T.Street)]
    public void HeadersThatOnlyContainATypeOrCompanyWord_KeepTheirOwnTarget(string header, T expected)
    {
        new ClientImportColumnDetector(_catalog).Detect([header], [], CurrentYear).Single().Target.ShouldBe(expected);
    }

    [Test]
    public async Task OwnerTestFileRows_GetTheirTypeAndCompany()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(
            [T.FirstName, T.LastName, T.Type, T.Company, T.Gender, T.Birthdate, T.Street, T.Zip, T.City, T.Contract],
            ["Dr. Peter Schneider", "Weiß", "Kunde", "Tech Systems GmbH", "divers", "20.12.1982", "Minervastrasse 111", "8008", "Zürich", ""],
            ["Victor", "Frey", "Extern", "", "männlich", "08.02.2000", "Johanniterbrücke 20", "4058", "Basel", ""],
            ["Sarah", "Haase", "Mitarbeiter", "", "männlich", "19.03.1962", "Bundesrain 4", "3018", "Bern", "Vollzeit"]);

        var drafts = await evaluator.EvaluateAsync(request, CancellationToken.None);

        drafts.ShouldAllBe(d => d.Status == ClientImportRowStatus.Ready);
        (drafts[0].Type, drafts[0].Company, drafts[0].LegalEntity, drafts[0].Gender)
            .ShouldBe((EntityTypeEnum.Customer, "Tech Systems GmbH", true, GenderEnum.LegalEntity));
        (drafts[0].FirstName, drafts[0].LastName).ShouldBe(("Dr. Peter Schneider", "Weiß"));
        drafts[0].Contract.ShouldBeNull();
        drafts[1].Type.ShouldBe(EntityTypeEnum.ExternEmp);
        drafts[1].Company.ShouldBeNull();
        drafts[2].Type.ShouldBe(EntityTypeEnum.Employee);
        drafts[2].Contract!.Id.ShouldBe(ClientImportTestData.ContractId);
    }

    [Test]
    public async Task EmptyType_IsAnEmployee_WithoutIssue()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(Person, ["Anna", "Muster", "w", "", ""]);

        var draft = (await evaluator.EvaluateAsync(request, CancellationToken.None)).Single();

        draft.Type.ShouldBe(EntityTypeEnum.Employee);
        draft.LegalEntity.ShouldBeFalse();
        draft.Status.ShouldBe(ClientImportRowStatus.Ready);
        Codes(draft).ShouldNotContain(ClientImportIssueCodes.UnknownType);
    }

    [Test]
    public async Task UnknownType_IsAnErrorNamingTheValue()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(Person, ["Anna", "Muster", "w", "Lieferant", ""]);

        var draft = (await evaluator.EvaluateAsync(request, CancellationToken.None)).Single();

        draft.Status.ShouldBe(ClientImportRowStatus.Error);
        var issue = draft.Issues.Single(i => i.Code == ClientImportIssueCodes.UnknownType);
        issue.Severity.ShouldBe(ClientImportIssueSeverity.Error);
        issue.Field.ShouldBe(ClientImportFields.Type);
        issue.Args[ClientImportIssueArgs.Value].ShouldBe("Lieferant");
    }

    [Test]
    public async Task CustomerWithCompanyButNoName_IsAValidLegalEntity()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(Company, ["", "", "Kunde", "Tech Systems GmbH", "Minervastrasse 111", "8008", "Zürich"]);

        var draft = (await evaluator.EvaluateAsync(request, CancellationToken.None)).Single();

        draft.Status.ShouldBe(ClientImportRowStatus.Ready);
        draft.LegalEntity.ShouldBeTrue();
        draft.Gender.ShouldBe(GenderEnum.LegalEntity);
        draft.Type.ShouldBe(EntityTypeEnum.Customer);
        Codes(draft).ShouldNotContain(ClientImportIssueCodes.MissingFirstName);
        Codes(draft).ShouldNotContain(ClientImportIssueCodes.MissingLastName);
        Codes(draft).ShouldNotContain(ClientImportIssueCodes.MissingGender);
    }

    [Test]
    public async Task LegalEntity_WithoutPostcodeAndCity_IsAnError()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(Company, ["", "", "Kunde", "Tech Systems GmbH", "Minervastrasse 111", "", ""]);

        var draft = (await evaluator.EvaluateAsync(request, CancellationToken.None)).Single();

        draft.Status.ShouldBe(ClientImportRowStatus.Error);
        draft.Issues.ShouldContain(i => i.Code == ClientImportIssueCodes.LegalEntityAddressIncomplete
                                         && i.Field == ClientImportFields.Zip
                                         && i.Severity == ClientImportIssueSeverity.Error);
    }

    [TestCase("Mitarbeiter")]
    [TestCase("Extern")]
    [TestCase("")]
    public async Task EmployeeOrExternalWithCompanyButNoName_KeepsTheNameRules(string type)
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(Company, ["", "", type, "Tech Systems GmbH", "Minervastrasse 111", "8008", "Zürich"]);

        var draft = (await evaluator.EvaluateAsync(request, CancellationToken.None)).Single();

        draft.LegalEntity.ShouldBeFalse();
        draft.Status.ShouldBe(ClientImportRowStatus.Error);
        Codes(draft).ShouldContain(ClientImportIssueCodes.MissingFirstName);
        Codes(draft).ShouldContain(ClientImportIssueCodes.MissingLastName);
        Codes(draft).ShouldContain(ClientImportIssueCodes.MissingGender);
    }

    [Test]
    public async Task CustomerWithCompanyAndPersonName_IsALegalEntity_WithTheNameAsContact_AndTheGenderIgnored()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(
            [T.Salutation, T.Title, T.FirstName, T.LastName, T.Gender, T.Type, T.Company, T.Street, T.Zip, T.City],
            ["Herr", "Dr.", "Lena", "Hesse", "divers", "Kunde", "Swiss Technologies AG", "Petersgasse 9", "4056", "Basel"],
            ["", "", "", "Hesse", "xyz", "Kunde", "Swiss Technologies AG", "Petersgasse 9", "4056", "Basel"]);
        request.RowOverrides.Add(new ClientImportRowOverride { RowIndex = 0, Gender = GenderEnum.Female });

        var drafts = await evaluator.EvaluateAsync(request, CancellationToken.None);

        drafts[0].Status.ShouldBe(ClientImportRowStatus.Ready);
        (drafts[0].LegalEntity, drafts[0].Gender).ShouldBe((true, (GenderEnum?)GenderEnum.LegalEntity));
        (drafts[0].Title, drafts[0].FirstName, drafts[0].LastName).ShouldBe(("Dr.", "Lena", "Hesse"));
        Codes(drafts[0]).ShouldNotContain(ClientImportIssueCodes.GenderFromSalutation);
        Codes(drafts[0]).ShouldNotContain(ClientImportIssueCodes.MissingGender);
        drafts[1].LegalEntity.ShouldBeTrue();
        Codes(drafts[1]).ShouldNotContain(ClientImportIssueCodes.MissingFirstName);
        Codes(drafts[1]).ShouldNotContain(ClientImportIssueCodes.MissingGender);

        var client = ClientImportClientFactory.Create(drafts[0], request.Policy);
        (client.LegalEntity, client.Gender, client.Company, client.Title, client.FirstName, client.Name)
            .ShouldBe((true, GenderEnum.LegalEntity, "Swiss Technologies AG", "Dr.", (string?)"Lena", "Hesse"));
    }

    [Test]
    public async Task CustomerWithoutCompany_StaysAPerson_AndNeedsNameAndGender()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(Person, ["Lena", "Hesse", "", "Kunde", ""], ["", "Hesse", "w", "Kunde", ""], ["Noa", "Kunz", "w", "Kunde", ""]);

        var drafts = await evaluator.EvaluateAsync(request, CancellationToken.None);

        drafts.ShouldAllBe(d => !d.LegalEntity && d.Type == EntityTypeEnum.Customer);
        Codes(drafts[0]).ShouldContain(ClientImportIssueCodes.MissingGender);
        Codes(drafts[1]).ShouldContain(ClientImportIssueCodes.MissingFirstName);
        drafts[2].Status.ShouldBe(ClientImportRowStatus.Ready);
        drafts[2].Gender.ShouldBe(GenderEnum.Female);
    }

    [Test]
    public async Task Customer_GetsNoContract_NeitherFromTheFileNorFromThePolicy()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(
            [T.FirstName, T.LastName, T.Gender, T.Type, T.Contract, T.Group],
            ["Lena", "Hesse", "w", "Kunde", "Vollzeit", "Zürich"],
            ["Noa", "Kunz", "w", "Kunde", "", ""],
            ["Max", "Muster", "m", "Mitarbeiter", "", ""]);
        request.Policy.ContractId = ClientImportTestData.ContractId;
        request.Policy.GroupId = ClientImportTestData.GroupId;

        var drafts = await evaluator.EvaluateAsync(request, CancellationToken.None);

        drafts[0].Contract.ShouldBeNull();
        drafts[0].Group!.Id.ShouldBe(ClientImportTestData.GroupId);
        drafts[0].Issues.ShouldContain(i => i.Code == ClientImportIssueCodes.ContractNotForCustomer
                                            && i.Severity == ClientImportIssueSeverity.Warning
                                            && i.Args[ClientImportIssueArgs.Value] == "Vollzeit");
        drafts[1].Contract.ShouldBeNull();
        Codes(drafts[1]).ShouldNotContain(ClientImportIssueCodes.ContractFromPolicy);
        Codes(drafts[1]).ShouldNotContain(ClientImportIssueCodes.ContractNotForCustomer);
        drafts[1].Group!.Id.ShouldBe(ClientImportTestData.GroupId);
        drafts[2].Contract!.Id.ShouldBe(ClientImportTestData.ContractId);
    }

    [Test]
    public async Task Factory_WritesTypeCompanyAndLegalEntity()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(
            [T.FirstName, T.LastName, T.Gender, T.Type, T.Company, T.Street, T.Zip, T.City, T.Contract],
            ["", "", "", "Kunde", "Tech Systems GmbH", "Minervastrasse 111", "8008", "Zürich", "Vollzeit"],
            ["Victor", "Frey", "m", "Extern", "Temp AG", "", "", "", ""]);

        var drafts = await evaluator.EvaluateAsync(request, CancellationToken.None);
        var company = ClientImportClientFactory.Create(drafts[0], request.Policy);
        var external = ClientImportClientFactory.Create(drafts[1], request.Policy);

        (company.Type, company.LegalEntity, company.Gender, company.Company, company.Name, company.FirstName)
            .ShouldBe((EntityTypeEnum.Customer, true, GenderEnum.LegalEntity, "Tech Systems GmbH", string.Empty, (string?)null));
        company.ClientContracts.ShouldBeEmpty();
        company.Membership.ShouldNotBeNull();
        company.Addresses.Single().Zip.ShouldBe("8008");
        (external.Type, external.LegalEntity, external.Company, external.Name)
            .ShouldBe((EntityTypeEnum.ExternEmp, false, "Temp AG", "Frey"));
    }

    [Test]
    public async Task Preview_ShowsTypeAndCompany()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(Company, ["", "", "Kunde", "Tech Systems GmbH", "Minervastrasse 111", "8008", "Zürich"]);

        var preview = ClientImportPreviewBuilder.Build(request, await evaluator.EvaluateAsync(request, CancellationToken.None));

        var record = preview.Rows.Single().Record;
        record.Type.ShouldBe(nameof(EntityTypeEnum.Customer));
        record.Company.ShouldBe("Tech Systems GmbH");
        record.Gender.ShouldBe(nameof(GenderEnum.LegalEntity));
    }

    [Test]
    public async Task Commit_WritesTypeAndCompany()
    {
        var clientRepository = Substitute.For<IClientRepository>();
        var unitOfWork = Substitute.For<IUnitOfWork>();
        var added = new List<Client>();
        clientRepository.Add(Arg.Do<Client>(added.Add)).Returns(Task.CompletedTask);
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>()).Returns(ci => ci.Arg<Func<Task<bool>>>()());
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var handler = new CommitClientImportCommandHandler(
            evaluator, clientRepository, Substitute.For<IClientImportBatchRepository>(), unitOfWork,
            Substitute.For<IEmailClientAssignmentService>(), Substitute.For<Klacks.Api.Application.Interfaces.Geocoding.IAddressGeocodingService>(),
            NullLogger<CommitClientImportCommandHandler>.Instance);
        var request = ClientImportTestData.Request(
            [T.FirstName, T.LastName, T.Gender, T.Type, T.Company, T.Street, T.Zip, T.City],
            ["", "", "", "Kunde", "Tech Systems GmbH", "Minervastrasse 111", "8008", "Zürich"],
            ["Lena", "Hesse", "w", "Kunde", "Swiss Technologies AG", "Petersgasse 9", "4056", "Basel"],
            ["Noa", "Kunz", "w", "Kunde", "", "", "", ""],
            ["Victor", "Frey", "m", "Extern", "", "", "", ""],
            ["Sarah", "Haase", "w", "", "", "", "", ""]);

        var result = await handler.Handle(new CommitClientImportCommand(request), CancellationToken.None);

        result.Created.ShouldBe(5);
        added.Select(c => (c.Type, c.LegalEntity, c.Company, c.Name)).ShouldBe([
            (EntityTypeEnum.Customer, true, "Tech Systems GmbH", string.Empty),
            (EntityTypeEnum.Customer, true, "Swiss Technologies AG", "Hesse"),
            (EntityTypeEnum.Customer, false, string.Empty, "Kunz"),
            (EntityTypeEnum.ExternEmp, false, string.Empty, "Frey"),
            (EntityTypeEnum.Employee, false, string.Empty, "Haase")]);
        added[1].Gender.ShouldBe(GenderEnum.LegalEntity);
    }

    [Test]
    public async Task LegalEntity_SameCompanyPostcodeAndStreetAsAnExistingCustomer_IsAConflict()
    {
        var existingId = Guid.NewGuid();
        var (evaluator, lookup) = ClientImportTestData.Evaluator(
            [ClientImportTestData.CompanyCustomer(existingId, "Tech Systems GmbH", "8008", "Minervastrasse 111")]);
        var request = ClientImportTestData.Request(Company,
            ["", "", "Kunde", " tech systems gmbh ", "MINERVASTRASSE 111", "8008", "Zürich"],
            ["", "", "Kunde", "Tech Systems GmbH", "Bahnhofstrasse 1", "8001", "Zürich"]);

        var drafts = await evaluator.EvaluateAsync(request, CancellationToken.None);

        drafts[0].Status.ShouldBe(ClientImportRowStatus.Skipped);
        drafts[0].DuplicateOfClientId.ShouldBe(existingId);
        drafts[0].DuplicateOfName.ShouldBe("Tech Systems GmbH");
        drafts[0].Issues.ShouldContain(i => i.Code == ClientImportIssueCodes.DuplicateInDatabase
                                            && i.Field == ClientImportFields.Company
                                            && i.Args[ClientImportIssueArgs.Reason] == ClientImportIssueArgs.ReasonSameCompany);
        drafts[1].Status.ShouldBe(ClientImportRowStatus.Ready);
        drafts[1].Issues.ShouldContain(i => i.Code == ClientImportIssueCodes.PossibleDuplicateName
                                            && i.Args[ClientImportIssueArgs.Reason] == ClientImportIssueArgs.ReasonSameCompany);
        await lookup.Received().FindDuplicateCandidatesAsync(
            Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Any<IReadOnlyCollection<string>>(),
            Arg.Is<IReadOnlyCollection<string>>(companies => companies.Contains("tech systems gmbh")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LegalEntity_TwiceInTheFile_IsAnInFileConflict()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(Company,
            ["", "", "Kunde", "Tech Systems GmbH", "Minervastrasse 111", "8008", "Zürich"],
            ["", "", "Kunde", "Tech Systems GmbH", "Minervastrasse 111", "8008", "Zürich"]);

        var drafts = await evaluator.EvaluateAsync(request, CancellationToken.None);

        drafts[0].Status.ShouldBe(ClientImportRowStatus.Ready);
        drafts[1].Status.ShouldBe(ClientImportRowStatus.Skipped);
        drafts[1].DuplicateOfRowIndex.ShouldBe(0);
        drafts[1].Issues.ShouldContain(i => i.Code == ClientImportIssueCodes.DuplicateInFile
                                            && i.Args[ClientImportIssueArgs.Reason] == ClientImportIssueArgs.ReasonSameCompany);
    }

    [Test]
    public async Task CustomersAndEmployees_AreNotDuplicatesOfEachOther()
    {
        var birthdate = new DateTime(1978, 7, 8, 0, 0, 0, DateTimeKind.Utc);
        var (evaluator, _) = ClientImportTestData.Evaluator(
            [ClientImportTestData.Employee(Guid.NewGuid(), "Lena", "Hesse", birthdate, "lena@example.com"),
             ClientImportTestData.PersonCustomer(Guid.NewGuid(), "Max", "Muster", null, "Muster AG", "max@example.com")]);
        var request = ClientImportTestData.Request(
            [T.FirstName, T.LastName, T.Gender, T.Type, T.Birthdate, T.Email],
            ["Lena", "Hesse", "w", "Kunde", "08.07.1978", "lena@example.com"],
            ["Max", "Muster", "m", "Mitarbeiter", "", "max@example.com"],
            ["Max", "Muster", "m", "Kunde", "", "max@example.com"]);

        var drafts = await evaluator.EvaluateAsync(request, CancellationToken.None);

        drafts[0].Status.ShouldBe(ClientImportRowStatus.Ready);
        drafts[0].DuplicateConflict.ShouldBeFalse();
        Codes(drafts[0]).ShouldNotContain(ClientImportIssueCodes.PossibleDuplicateName);
        drafts[1].Status.ShouldBe(ClientImportRowStatus.Ready);
        drafts[1].DuplicateConflict.ShouldBeFalse();
        drafts[2].Status.ShouldBe(ClientImportRowStatus.Skipped);
        Codes(drafts[2]).ShouldContain(ClientImportIssueCodes.DuplicateInDatabase);
    }

    [Test]
    public void Template_TypeColumnCarriesTheTypeWordsAsNote()
    {
        var headers = ClientImportTemplateLayout.Headers(_catalog, "de");
        var notes = ClientImportTemplateLayout.HeaderNotes(_catalog, "de");

        headers.ShouldContain("Typ");
        headers.ShouldContain("Firma");
        notes[headers.IndexOf("Typ")].ShouldBe("Mitarbeiter / Extern / Kunde");
        notes.Count(n => n != null).ShouldBe(1);

        var content = new Klacks.Api.Infrastructure.Services.ClientImport.ClientImportTemplateBuilder()
            .Build(ClientImportTemplateLayout.SheetName, headers, notes);
        using var workbook = new ClosedXML.Excel.XLWorkbook(new MemoryStream(content));
        var cell = workbook.Worksheet(1).Cell(1, headers.IndexOf("Typ") + 1);
        cell.HasComment.ShouldBeTrue();
        cell.GetComment().Text.ShouldBe("Mitarbeiter / Extern / Kunde");
    }

    private static IEnumerable<string> Codes(ClientImportDraft draft) => draft.Issues.Select(i => i.Code);
}
