// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The shared preview/commit evaluation of the employee import: required fields, gender from salutation
/// or per-row choice, policy fallbacks (entry date, contract, group, country), former employees, date
/// errors, duplicates in the database and in the file, and the server-side request guard.
/// </summary>

using Klacks.Api.Application.DTOs.ClientImport;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Services.ClientImport;
using Klacks.Api.Domain.Constants;
using T = Klacks.Api.Domain.Enums.ClientImportTarget;

namespace Klacks.UnitTest.ClientImport;

[TestFixture]
public class ClientImportEvaluatorTests
{
    private static readonly T[] Basic = [T.FirstName, T.LastName, T.Gender];

    [Test]
    public async Task CompleteRow_IsReady_WithPolicyEntryDateToday()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(Basic, ["Anna", "Muster", "w"]);

        var draft = (await evaluator.EvaluateAsync(request, CancellationToken.None)).Single();

        draft.Status.ShouldBe(ClientImportRowStatus.Ready);
        draft.Gender.ShouldBe(GenderEnum.Female);
        draft.EntryDate.ShouldBe(ClientImportTestData.Today);
        draft.CountryCode.ShouldBe("CH");
        Codes(draft).ShouldContain(ClientImportIssueCodes.EntryDateFromPolicy);
        Codes(draft).ShouldContain(ClientImportIssueCodes.MissingAddress);
    }

    [Test]
    public async Task MissingNamesAndGender_AreErrors()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(Basic, ["", " ", ""]);

        var draft = (await evaluator.EvaluateAsync(request, CancellationToken.None)).Single();

        draft.Status.ShouldBe(ClientImportRowStatus.Error);
        draft.Issues.Where(i => i.Severity == ClientImportIssueSeverity.Error).Select(i => i.Code).ShouldBe(
            [ClientImportIssueCodes.MissingFirstName, ClientImportIssueCodes.MissingLastName, ClientImportIssueCodes.MissingGender],
            ignoreOrder: true);
    }

    [Test]
    public async Task Gender_ComesFromSalutation_OrFromTheRowOverride()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request([T.Salutation, T.FirstName, T.LastName], ["Herr", "Max", "Muster"], ["", "Kim", "Muster"]);
        request.RowOverrides.Add(new ClientImportRowOverride { RowIndex = 1, Gender = GenderEnum.Intersexuality });

        var drafts = await evaluator.EvaluateAsync(request, CancellationToken.None);

        drafts[0].Gender.ShouldBe(GenderEnum.Male);
        Codes(drafts[0]).ShouldContain(ClientImportIssueCodes.GenderFromSalutation);
        drafts[1].Gender.ShouldBe(GenderEnum.Intersexuality);
        drafts[1].Status.ShouldBe(ClientImportRowStatus.Ready);
    }

    [Test]
    public async Task FullName_AndZipCity_AreSplit()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request([T.FullName, T.ZipCity, T.Gender], ["Müller, Hans", "8000 Zürich", "m"]);

        var draft = (await evaluator.EvaluateAsync(request, CancellationToken.None)).Single();

        draft.FirstName.ShouldBe("Hans");
        draft.LastName.ShouldBe("Müller");
        draft.Zip.ShouldBe("8000");
        draft.City.ShouldBe("Zürich");
        Codes(draft).ShouldContain(ClientImportIssueCodes.ZipCitySplit);
        Codes(draft).ShouldNotContain(ClientImportIssueCodes.MissingAddress);
    }

    [Test]
    public async Task Dates_AreParsed_AndInvalidOrReversedDatesAreErrors()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(
            [T.FirstName, T.LastName, T.Gender, T.Birthdate, T.EntryDate, T.ExitDate],
            ["Anna", "A", "w", "05.03.1990", "01.04.2020", ""],
            ["Ben", "B", "m", "31.02.1990", "01.04.2020", ""],
            ["Cleo", "C", "w", "05.03.1990", "01.04.2027", "01.01.2027"]);
        request.Policy.FormerEmployees = ClientImportFormerEmployeesMode.ImportWithExitDate;

        var drafts = await evaluator.EvaluateAsync(request, CancellationToken.None);

        drafts[0].Birthdate.ShouldBe(new DateTime(1990, 3, 5, 0, 0, 0, DateTimeKind.Utc));
        drafts[0].EntryDate.ShouldBe(new DateTime(2020, 4, 1, 0, 0, 0, DateTimeKind.Utc));
        drafts[0].Status.ShouldBe(ClientImportRowStatus.Ready);
        drafts[1].Status.ShouldBe(ClientImportRowStatus.Error);
        Codes(drafts[1]).ShouldContain(ClientImportIssueCodes.InvalidDate);
        drafts[2].Status.ShouldBe(ClientImportRowStatus.Error);
        drafts[2].Issues.ShouldContain(i => i.Code == ClientImportIssueCodes.InvalidDate
                                            && i.Args[ClientImportIssueArgs.Reason] == ClientImportIssueArgs.ReasonExitBeforeEntry);
    }

    [Test]
    public async Task FormerEmployee_IsSkipped_OrImportedWithExitDate()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request([T.FirstName, T.LastName, T.Gender, T.EntryDate, T.ExitDate], ["Anna", "A", "w", "01.01.2020", "31.12.2025"]);

        var skipped = (await evaluator.EvaluateAsync(request, CancellationToken.None)).Single();
        request.Policy.FormerEmployees = ClientImportFormerEmployeesMode.ImportWithExitDate;
        var imported = (await evaluator.EvaluateAsync(request, CancellationToken.None)).Single();

        skipped.Status.ShouldBe(ClientImportRowStatus.Skipped);
        Codes(skipped).ShouldContain(ClientImportIssueCodes.FormerEmployeeSkipped);
        imported.Status.ShouldBe(ClientImportRowStatus.Ready);
        imported.ExitDate.ShouldBe(new DateTime(2025, 12, 31, 0, 0, 0, DateTimeKind.Utc));
    }

    [Test]
    public async Task ContractAndGroup_AreMatchedByName_WithPolicyFallback()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(
            [T.FirstName, T.LastName, T.Gender, T.Contract, T.Group],
            ["Anna", "A", "w", "vollzeit", "ZÜRICH"],
            ["Ben", "B", "m", "Unbekannt", "Bern"]);
        request.Policy.ContractId = ClientImportTestData.ContractId;

        var drafts = await evaluator.EvaluateAsync(request, CancellationToken.None);

        drafts[0].Contract!.Id.ShouldBe(ClientImportTestData.ContractId);
        drafts[0].Group!.Id.ShouldBe(ClientImportTestData.GroupId);
        Codes(drafts[1]).ShouldContain(ClientImportIssueCodes.UnknownContract);
        Codes(drafts[1]).ShouldContain(ClientImportIssueCodes.ContractFromPolicy);
        drafts[1].Contract!.Id.ShouldBe(ClientImportTestData.ContractId);
        drafts[1].Issues.ShouldContain(i => i.Code == ClientImportIssueCodes.UnknownGroup && i.Args[ClientImportIssueArgs.Reason] == ClientImportIssueArgs.ReasonAmbiguous);
        drafts[1].Group.ShouldBeNull();
        drafts[1].Status.ShouldBe(ClientImportRowStatus.Ready);
    }

    [Test]
    public async Task Country_AndPhoneUseTheRowCountry_UnknownCountryFallsBackWithWarning()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(
            [T.FirstName, T.LastName, T.Gender, T.City, T.Country, T.Mobile],
            ["Anna", "A", "w", "Berlin", "Deutschland", "0170 1234567"],
            ["Ben", "B", "m", "Bern", "Atlantis", "079 123 45 67"]);

        var drafts = await evaluator.EvaluateAsync(request, CancellationToken.None);

        drafts[0].CountryCode.ShouldBe("DE");
        (drafts[0].MobilePrefix, drafts[0].MobileNumber).ShouldBe(("+49", "1701234567"));
        drafts[1].CountryCode.ShouldBe("CH");
        Codes(drafts[1]).ShouldContain(ClientImportIssueCodes.UnknownCountry);
    }

    [Test]
    public async Task InvalidEmail_IsAWarning_AndNotImported()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request([T.FirstName, T.LastName, T.Gender, T.Email], ["Anna", "A", "w", "anna(at)example"]);

        var draft = (await evaluator.EvaluateAsync(request, CancellationToken.None)).Single();

        draft.Email.ShouldBeNull();
        draft.Status.ShouldBe(ClientImportRowStatus.Ready);
        Codes(draft).ShouldContain(ClientImportIssueCodes.InvalidEmail);
    }

    [Test]
    public async Task TooLongName_IsAnError()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(Basic, [new string('a', ClientImportLimits.MaxPersonFieldLength + 1), "A", "w"]);

        var draft = (await evaluator.EvaluateAsync(request, CancellationToken.None)).Single();

        Codes(draft).ShouldContain(ClientImportIssueCodes.ValueTooLong);
        draft.Status.ShouldBe(ClientImportRowStatus.Error);
    }

    [Test]
    public async Task DuplicateInDatabase_ByEmail_IsSkippedByDefault_AndCanBeCreatedAnyway()
    {
        var existingId = Guid.NewGuid();
        var (evaluator, lookup) = ClientImportTestData.Evaluator(
            [new ClientImportExistingClient(existingId, "Anna", "Alt", null, ["ANNA@example.com"])]);
        var request = ClientImportTestData.Request([T.FirstName, T.LastName, T.Gender, T.Email], ["Anna", "Neu", "w", "anna@example.com"]);

        var skipped = (await evaluator.EvaluateAsync(request, CancellationToken.None)).Single();
        request.RowOverrides.Add(new ClientImportRowOverride { RowIndex = 0, CreateDuplicate = true });
        var created = (await evaluator.EvaluateAsync(request, CancellationToken.None)).Single();

        skipped.Status.ShouldBe(ClientImportRowStatus.Skipped);
        skipped.DuplicateOfClientId.ShouldBe(existingId);
        skipped.DuplicateOfName.ShouldBe("Anna Alt");
        Codes(skipped).ShouldContain(ClientImportIssueCodes.DuplicateInDatabase);
        created.Status.ShouldBe(ClientImportRowStatus.Ready);
        created.DuplicateConflict.ShouldBeTrue();
        await lookup.Received().FindDuplicateCandidatesAsync(
            Arg.Is<IReadOnlyCollection<string>>(names => names.Contains("neu")),
            Arg.Is<IReadOnlyCollection<string>>(mails => mails.Contains("anna@example.com")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DuplicateInDatabase_ByNameAndBirthdate_IsAConflict_NameAloneOnlyAHint()
    {
        var birthdate = new DateTime(1990, 3, 5, 0, 0, 0, DateTimeKind.Utc);
        var (evaluator, _) = ClientImportTestData.Evaluator(
            [new ClientImportExistingClient(Guid.NewGuid(), "Anna", "Müller", birthdate, []),
             new ClientImportExistingClient(Guid.NewGuid(), "Ben", "Meier", null, [])]);
        var request = ClientImportTestData.Request(
            [T.FirstName, T.LastName, T.Gender, T.Birthdate],
            ["anna", "MÜLLER", "w", "05.03.1990"],
            ["Ben", "Meier", "m", "01.01.1980"]);

        var drafts = await evaluator.EvaluateAsync(request, CancellationToken.None);

        drafts[0].Status.ShouldBe(ClientImportRowStatus.Skipped);
        Codes(drafts[0]).ShouldContain(ClientImportIssueCodes.DuplicateInDatabase);
        drafts[1].Status.ShouldBe(ClientImportRowStatus.Ready);
        Codes(drafts[1]).ShouldContain(ClientImportIssueCodes.PossibleDuplicateName);
    }

    [Test]
    public async Task DuplicateInFile_PointsAtTheEarlierRow_UnlessPolicyCreatesAnyway()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(
            [T.FirstName, T.LastName, T.Gender, T.Email],
            ["Anna", "A", "w", "anna@example.com"],
            ["Anna", "B", "w", "Anna@Example.com"]);

        var drafts = await evaluator.EvaluateAsync(request, CancellationToken.None);
        request.Policy.Duplicates = ClientImportDuplicateMode.CreateAnyway;
        var anyway = await evaluator.EvaluateAsync(request, CancellationToken.None);

        drafts[0].Status.ShouldBe(ClientImportRowStatus.Ready);
        drafts[1].Status.ShouldBe(ClientImportRowStatus.Skipped);
        drafts[1].DuplicateOfRowIndex.ShouldBe(0);
        Codes(drafts[1]).ShouldContain(ClientImportIssueCodes.DuplicateInFile);
        anyway[1].Status.ShouldBe(ClientImportRowStatus.Ready);
    }

    [Test]
    public async Task RehiredEmployee_SkippedFormerStint_DoesNotSuppressTheCurrentOne()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(
            [T.FirstName, T.LastName, T.Gender, T.Email, T.EntryDate, T.ExitDate],
            ["Anna", "A", "w", "anna@example.com", "01.01.2015", "31.12.2018"],
            ["Anna", "A", "w", "anna@example.com", "01.01.2022", ""]);

        var drafts = await evaluator.EvaluateAsync(request, CancellationToken.None);

        drafts[0].Status.ShouldBe(ClientImportRowStatus.Skipped);
        Codes(drafts[0]).ShouldContain(ClientImportIssueCodes.FormerEmployeeSkipped);
        drafts[1].Status.ShouldBe(ClientImportRowStatus.Ready);
        drafts[1].DuplicateConflict.ShouldBeFalse();
    }

    [Test]
    public async Task ThirdRow_IsComparedWithTheWrittenRow_NotWithASkippedDuplicate()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(
            [T.FirstName, T.LastName, T.Gender, T.Email],
            ["Anna", "A", "w", "anna@example.com"],
            ["Anna", "B", "w", "anna@example.com"],
            ["Anna", "C", "w", "anna@example.com"]);

        var drafts = await evaluator.EvaluateAsync(request, CancellationToken.None);

        drafts[1].DuplicateOfRowIndex.ShouldBe(0);
        drafts[2].DuplicateOfRowIndex.ShouldBe(0);
    }

    [Test]
    public async Task UserSkippedRow_IsSkipped_EvenWithErrors()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(Basic, ["", "", ""]);
        request.RowOverrides.Add(new ClientImportRowOverride { RowIndex = 0, Skip = true });

        (await evaluator.EvaluateAsync(request, CancellationToken.None)).Single().Status.ShouldBe(ClientImportRowStatus.Skipped);
    }

    [Test]
    public async Task PersonnelNumber_IsReportedAsNotImported()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request([T.FirstName, T.LastName, T.Gender, T.PersonnelNumber], ["Anna", "A", "w", "4711"]);

        var drafts = await evaluator.EvaluateAsync(request, CancellationToken.None);
        var preview = ClientImportPreviewBuilder.Build(request, drafts);

        Codes(drafts[0]).ShouldContain(ClientImportIssueCodes.PersonnelNumberNotImported);
        preview.IgnoredTargets.ShouldBe([T.PersonnelNumber]);
        preview.Summary.Ready.ShouldBe(1);
    }

    [Test]
    public async Task Preview_CountsStatusesAndListsUnmappedColumns()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(
            [T.FirstName, T.LastName, T.Gender, T.Ignore, T.Email],
            ["Anna", "A", "w", "x", "anna(at)x"],
            ["", "B", "m", "y", ""],
            ["Cleo", "C", "w", "z", ""]);
        request.RowOverrides.Add(new ClientImportRowOverride { RowIndex = 2, Skip = true });

        var preview = ClientImportPreviewBuilder.Build(request, await evaluator.EvaluateAsync(request, CancellationToken.None));

        var summary = preview.Summary;
        (summary.Total, summary.Ready, summary.Errors, summary.Skipped, summary.Duplicates, summary.Warnings).ShouldBe((3, 1, 1, 1, 0, 1));
        preview.UnmappedColumns.ShouldBe([3]);
        preview.Rows[0].Record.Email.ShouldBeNull();
        preview.Rows[0].Record.EntryDate.ShouldBe("2026-09-30");
        preview.Rows[0].Record.Gender.ShouldBe("Female");
    }

    [Test]
    public async Task ImplausibleEntryDate_IsAWarning()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request([T.FirstName, T.LastName, T.Gender, T.Birthdate, T.EntryDate], ["Anna", "A", "w", "05.03.1990", "01.01.1980"]);

        var draft = (await evaluator.EvaluateAsync(request, CancellationToken.None)).Single();

        Codes(draft).ShouldContain(ClientImportIssueCodes.EntryDateImplausible);
        draft.Status.ShouldBe(ClientImportRowStatus.Ready);
    }

    [TestCase(ClientImportLimits.MaxRows + 1, 3, ClientImportErrorCodes.TooManyRows)]
    [TestCase(0, 3, ClientImportErrorCodes.FileEmpty)]
    public async Task RequestGuard_RejectsGridsOutsideTheLimits(int rowCount, int columnCount, string code)
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(Basic, Enumerable.Range(0, rowCount).Select(_ => new[] { "a", "b", "w" }).ToArray());

        var exception = await Should.ThrowAsync<ClientImportRejectedException>(() => evaluator.EvaluateAsync(request, CancellationToken.None));

        exception.Code.ShouldBe(code);
    }

    [Test]
    public async Task RequestGuard_RejectsDuplicateTargetsAndBadPolicies()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();

        var doubled = ClientImportTestData.Request([T.FirstName, T.FirstName], ["a", "b"]);
        (await Should.ThrowAsync<ClientImportRejectedException>(() => evaluator.EvaluateAsync(doubled, CancellationToken.None)))
            .Code.ShouldBe(ClientImportErrorCodes.InvalidRequest);

        var badType = ClientImportTestData.Request(Basic, ["a", "b", "w"]);
        badType.Policy.EmailType = CommunicationTypeEnum.PrivateCellPhone;
        (await Should.ThrowAsync<ClientImportRejectedException>(() => evaluator.EvaluateAsync(badType, CancellationToken.None)))
            .Code.ShouldBe(ClientImportErrorCodes.InvalidPolicy);

        var unknownContract = ClientImportTestData.Request(Basic, ["a", "b", "w"]);
        unknownContract.Policy.ContractId = Guid.NewGuid();
        (await Should.ThrowAsync<ClientImportRejectedException>(() => evaluator.EvaluateAsync(unknownContract, CancellationToken.None)))
            .Code.ShouldBe(ClientImportErrorCodes.InvalidPolicy);

        var legalEntity = ClientImportTestData.Request(Basic, ["a", "b", "w"]);
        legalEntity.RowOverrides.Add(new ClientImportRowOverride { RowIndex = 0, Gender = GenderEnum.LegalEntity });
        (await Should.ThrowAsync<ClientImportRejectedException>(() => evaluator.EvaluateAsync(legalEntity, CancellationToken.None)))
            .Code.ShouldBe(ClientImportErrorCodes.InvalidRequest);

        var badDate = ClientImportTestData.Request(Basic, ["a", "b", "w"]);
        badDate.Policy.EntryDate = "01.02.2026";
        (await Should.ThrowAsync<ClientImportRejectedException>(() => evaluator.EvaluateAsync(badDate, CancellationToken.None)))
            .Code.ShouldBe(ClientImportErrorCodes.InvalidPolicy);
    }

    [Test]
    public async Task RequestGuard_RejectsDoubledOrSurplusOverridesAndAMissingToken()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();

        var doubled = ClientImportTestData.Request(Basic, ["a", "b", "w"], ["c", "d", "m"]);
        doubled.RowOverrides.Add(new ClientImportRowOverride { RowIndex = 0, Skip = true });
        doubled.RowOverrides.Add(new ClientImportRowOverride { RowIndex = 0, Skip = false });
        (await Should.ThrowAsync<ClientImportRejectedException>(() => evaluator.EvaluateAsync(doubled, CancellationToken.None)))
            .Code.ShouldBe(ClientImportErrorCodes.InvalidRequest);

        var surplus = ClientImportTestData.Request(Basic, ["a", "b", "w"]);
        surplus.RowOverrides.Add(new ClientImportRowOverride { RowIndex = 0 });
        surplus.RowOverrides.Add(new ClientImportRowOverride { RowIndex = 1 });
        (await Should.ThrowAsync<ClientImportRejectedException>(() => evaluator.EvaluateAsync(surplus, CancellationToken.None)))
            .Code.ShouldBe(ClientImportErrorCodes.InvalidRequest);

        var noToken = ClientImportTestData.Request(Basic, ["a", "b", "w"]);
        noToken.Token = Guid.Empty;
        (await Should.ThrowAsync<ClientImportRejectedException>(() => evaluator.EvaluateAsync(noToken, CancellationToken.None)))
            .Code.ShouldBe(ClientImportErrorCodes.InvalidRequest);
    }

    [Test]
    public async Task RequestGuard_RejectsAGridAboveTheCharacterBudget()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        const int cellsPerRow = 10;
        var cell = new string('x', ClientImportLimits.MaxCellLength);
        var rowCount = (int)(ClientImportLimits.MaxGridChars / (ClientImportLimits.MaxCellLength * cellsPerRow)) + 1;
        var request = ClientImportTestData.Request(Basic, Enumerable.Range(0, rowCount).Select(_ => Enumerable.Repeat(cell, cellsPerRow).ToArray()).ToArray());

        rowCount.ShouldBeLessThanOrEqualTo(ClientImportLimits.MaxRows);
        (await Should.ThrowAsync<ClientImportRejectedException>(() => evaluator.EvaluateAsync(request, CancellationToken.None)))
            .Code.ShouldBe(ClientImportErrorCodes.InvalidRequest);
    }

    [Test]
    public async Task PolicyEntryDate_IsUsedWhenTheRowHasNone()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(Basic, ["Anna", "A", "w"]);
        request.Policy.EntryDate = "2026-11-01";

        var draft = (await evaluator.EvaluateAsync(request, CancellationToken.None)).Single();

        draft.EntryDate.ShouldBe(new DateTime(2026, 11, 1, 0, 0, 0, DateTimeKind.Utc));
        draft.EntryDate.Kind.ShouldBe(DateTimeKind.Utc);
    }

    private static List<string> Codes(ClientImportDraft draft) => draft.Issues.Select(i => i.Code).ToList();
}
