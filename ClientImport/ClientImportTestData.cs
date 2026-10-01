// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Builds import requests and an evaluator with substituted lookups for the employee import tests.
/// </summary>

using Klacks.Api.Application.DTOs.ClientImport;
using Klacks.Api.Application.Interfaces.ClientImport;
using Klacks.Api.Application.Services.ClientImport;
using Klacks.Api.Domain.Common;

namespace Klacks.UnitTest.ClientImport;

internal static class ClientImportTestData
{
    public static readonly DateTime Today = new(2026, 9, 30, 0, 0, 0, DateTimeKind.Utc);
    public static readonly Guid ContractId = Guid.NewGuid();
    public static readonly Guid GroupId = Guid.NewGuid();

    public static readonly Countries Switzerland = new()
    {
        Id = Guid.NewGuid(),
        Abbreviation = "CH",
        Prefix = "+41",
        Name = new MultiLanguage { De = "Schweiz", En = "Switzerland" }
    };

    public static readonly Countries Germany = new()
    {
        Id = Guid.NewGuid(),
        Abbreviation = "DE",
        Prefix = "+49",
        Name = new MultiLanguage { De = "Deutschland", En = "Germany" }
    };

    public static ClientImportRequest Request(ClientImportTarget[] targets, params string[][] rows) => new()
    {
        Token = Guid.NewGuid(),
        FileName = "staff.csv",
        Columns = targets.Select((t, i) => new ClientImportColumn { Index = i, Header = t.ToString() }).ToList(),
        Rows = rows.Select(r => r.ToList()).ToList(),
        Mapping = targets.Select((t, i) => new ClientImportColumnMapping { ColumnIndex = i, Target = t, Confidence = 1 }).ToList(),
        DateFormat = ClientImportDateFormat.DayMonthYear,
        NameOrder = ClientImportNameOrder.FirstLast,
        Policy = new ClientImportPolicy()
    };

    public static (ClientImportEvaluator Evaluator, IClientImportLookupRepository Lookup) Evaluator(
        List<ClientImportExistingClient>? existing = null)
    {
        var lookup = Substitute.For<IClientImportLookupRepository>();
        lookup.GetContractsAsync(Arg.Any<CancellationToken>())
            .Returns([new ClientImportNamedEntity(ContractId, "Vollzeit"), new ClientImportNamedEntity(Guid.NewGuid(), "Teilzeit")]);
        lookup.GetGroupsAsync(Arg.Any<CancellationToken>())
            .Returns([new ClientImportNamedEntity(GroupId, "Zürich"), new ClientImportNamedEntity(Guid.NewGuid(), "Bern"), new ClientImportNamedEntity(Guid.NewGuid(), "Bern")]);
        lookup.FindDuplicateCandidatesAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(existing ?? []);

        var countries = Substitute.For<ICountryResolver>();
        countries.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(Switzerland);
        countries.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>()).Returns(ci =>
        {
            var value = ci.Arg<string?>();
            return value switch
            {
                "CH" or "Schweiz" => Switzerland,
                "DE" or "Deutschland" => Germany,
                _ => (Countries?)null
            };
        });

        var clock = Substitute.For<ICompanyClock>();
        clock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(Today);

        var evaluator = new ClientImportEvaluator(lookup, countries, clock, new ClientImportTransformer(ClientImportSynonymCatalog.LoadEmbedded()));
        return (evaluator, lookup);
    }
}
