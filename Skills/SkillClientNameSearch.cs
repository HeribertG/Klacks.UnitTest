// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Skills;

/// <summary>
/// Client search stubs for the write skills that resolve a person by first and last name: a search repository that
/// finds the given rows, and a client repository that loads the entity behind a found row.
/// </summary>
internal static class SkillClientNameSearch
{
    public static IClientSearchRepository Finding(params ClientSearchItem[] items)
    {
        var search = Substitute.For<IClientSearchRepository>();
        search.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult { Items = items.ToList(), TotalCount = items.Length });
        return search;
    }

    public static ClientSearchItem Row(Guid id, string firstName, string lastName, int idNumber) =>
        new() { Id = id, FirstName = firstName, LastName = lastName, IdNumber = idNumber };

    public static void Loads(IClientRepository clientRepository, Guid id, string firstName, string lastName, int idNumber) =>
        clientRepository.Get(id).Returns(new Client { Id = id, FirstName = firstName, Name = lastName, IdNumber = idNumber });
}
