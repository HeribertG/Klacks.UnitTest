// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Preview answer of the employee import: the summary counts, the unmapped columns and the mapped
/// targets that are not imported, and the parity promise that every ready preview record shows exactly
/// what ClientImportClientFactory writes for that row (name, address with state and country, e-mail,
/// phone, mobile, entry and exit date, contract and group).
/// </summary>

using Klacks.Api.Application.DTOs.ClientImport;
using Klacks.Api.Application.Services.ClientImport;
using Klacks.Api.Domain.Constants;
using T = Klacks.Api.Domain.Enums.ClientImportTarget;

namespace Klacks.UnitTest.ClientImport;

[TestFixture]
public class ClientImportPreviewBuilderTests
{
    [Test]
    public void Summary_CountsEveryStatusDuplicatesAndWarnings()
    {
        var request = ClientImportTestData.Request([T.FirstName, T.LastName], ["a", "b"], ["c", "d"], ["e", "f"], ["g", "h"]);
        var ready = new ClientImportDraft { RowIndex = 0 };
        ready.AddIssue(null, ClientImportIssueSeverity.Warning, ClientImportIssueCodes.InvalidEmail);
        var error = new ClientImportDraft { RowIndex = 1 };
        error.AddIssue(null, ClientImportIssueSeverity.Error, ClientImportIssueCodes.MissingGender);
        var duplicateSkipped = new ClientImportDraft { RowIndex = 2, DuplicateConflict = true, DuplicateSkipped = true };
        var duplicateCreated = new ClientImportDraft { RowIndex = 3, DuplicateConflict = true };

        var summary = ClientImportPreviewBuilder.Build(request, [ready, error, duplicateSkipped, duplicateCreated]).Summary;

        (summary.Total, summary.Ready, summary.Errors, summary.Skipped, summary.Duplicates, summary.Warnings)
            .ShouldBe((4, 2, 1, 1, 2, 1));
    }

    [Test]
    public void UnmappedColumns_AndIgnoredTargets_AreListed()
    {
        var request = ClientImportTestData.Request(
            [T.FirstName, T.Ignore, T.LastName, T.PersonnelNumber, T.Ignore],
            ["a", "x", "b", "4711", "y"]);
        request.Columns.Add(new ClientImportColumn { Index = 5, Header = "Unmapped" });

        var preview = ClientImportPreviewBuilder.Build(request, [new ClientImportDraft { RowIndex = 0 }]);

        preview.UnmappedColumns.ShouldBe([1, 4, 5]);
        preview.IgnoredTargets.ShouldBe([T.PersonnelNumber]);
    }

    [Test]
    public async Task ReadyRecords_ShowExactlyWhatTheFactoryWrites()
    {
        var (evaluator, _) = ClientImportTestData.Evaluator();
        var request = ClientImportTestData.Request(
            [T.FirstName, T.LastName, T.Gender, T.Street, T.Zip, T.City, T.State, T.Country, T.Email, T.Phone, T.Mobile,
             T.EntryDate, T.ExitDate, T.Contract, T.Group],
            ["Anna", "Muster", "w", "Hauptstrasse 1", "10115", "Berlin", "BE", "Deutschland", "anna@example.com",
             "030 1234567", "0170 1234567", "01.03.2024", "31.12.2026", "Vollzeit", "Zürich"],
            ["Ben", "Ohne", "m", "", "", "", "ZH", "Deutschland", "", "", "", "", "", "", ""]);
        request.Policy.FormerEmployees = ClientImportFormerEmployeesMode.ImportWithExitDate;

        var drafts = await evaluator.EvaluateAsync(request, CancellationToken.None);
        var preview = ClientImportPreviewBuilder.Build(request, drafts);

        drafts.ShouldAllBe(d => d.Status == ClientImportRowStatus.Ready);
        foreach (var draft in drafts)
        {
            AssertParity(preview.Rows[draft.RowIndex].Record, draft, ClientImportClientFactory.Create(draft, request.Policy), request.Policy);
        }

        preview.Rows[1].Record.Country.ShouldBeNull();
        preview.Rows[1].Record.State.ShouldBeNull();
    }

    private static void AssertParity(ClientImportRecord record, ClientImportDraft draft, Client client, ClientImportPolicy policy)
    {
        record.FirstName.ShouldBe(client.FirstName);
        record.LastName.ShouldBe(client.Name);

        var address = client.Addresses.SingleOrDefault();
        record.Street.ShouldBe(NullIfEmpty(address?.Street));
        record.Zip.ShouldBe(NullIfEmpty(address?.Zip));
        record.City.ShouldBe(NullIfEmpty(address?.City));
        record.State.ShouldBe(NullIfEmpty(address?.State));
        record.Country.ShouldBe(NullIfEmpty(address?.Country));

        record.Email.ShouldBe(Communication(client, policy.EmailType));
        record.Phone.ShouldBe(Communication(client, policy.PhoneType));
        record.Mobile.ShouldBe(Communication(client, policy.MobileType));

        record.EntryDate.ShouldBe(ClientImportDateParser.Format(client.Membership!.ValidFrom));
        record.ExitDate.ShouldBe(client.Membership.ValidUntil.HasValue ? ClientImportDateParser.Format(client.Membership.ValidUntil.Value) : null);

        record.ContractName.ShouldBe(draft.Contract?.Name);
        ((Guid?)client.ClientContracts.SingleOrDefault()?.ContractId).ShouldBe(draft.Contract?.Id);
        (client.ClientContracts.Count == 0).ShouldBe(draft.Contract == null);

        record.GroupName.ShouldBe(draft.Group?.Name);
        ((Guid?)client.GroupItems.SingleOrDefault()?.GroupId).ShouldBe(draft.Group?.Id);
        (client.GroupItems.Count == 0).ShouldBe(draft.Group == null);
    }

    private static string? Communication(Client client, CommunicationTypeEnum type)
    {
        var communication = client.Communications.SingleOrDefault(c => c.Type == type);
        if (communication == null)
        {
            return null;
        }

        return string.IsNullOrEmpty(communication.Prefix) ? communication.Value : string.Concat(communication.Prefix, " ", communication.Value);
    }

    private static string? NullIfEmpty(string? value) => string.IsNullOrEmpty(value) ? null : value;
}
