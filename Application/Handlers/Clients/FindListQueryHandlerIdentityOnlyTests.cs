// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The duplicate check is not limited by group visibility, so it may only reveal identity. Owner decision
/// 2026-10-01: name, first name, company and id number — no birthdate, maiden name, gender or addresses.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Handlers.Clients;
using Klacks.Api.Application.Queries.Clients;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Handlers.Clients;

[TestFixture]
public class FindListQueryHandlerIdentityOnlyTests
{
    [Test]
    public async Task Handle_ReturnsIdentityFieldsOnly()
    {
        var repository = Substitute.For<IClientSearchRepository>();
        var client = new Client
        {
            Id = Guid.NewGuid(),
            IdNumber = 4711,
            Company = "Wachdienst AG",
            Name = "Steinmann",
            FirstName = "Petra",
            MaidenName = "Muster",
            Birthdate = new DateTime(1980, 5, 1, 0, 0, 0, DateTimeKind.Utc),
            Type = EntityTypeEnum.Employee
        };
        repository.FindList("Wach", null, null).Returns([client]);
        var handler = new FindListQueryHandler(repository, TestGroupWriteVisibility.AllClientsVisible());

        var result = (await handler.Handle(new FindListQuery("Wach"), CancellationToken.None)).Single();
        var json = JsonSerializer.Serialize(result);

        result.Id.ShouldBe(client.Id);
        result.IdNumber.ShouldBe(4711);
        result.Name.ShouldBe("Steinmann");
        result.FirstName.ShouldBe("Petra");
        result.Company.ShouldBe("Wachdienst AG");
        json.ShouldNotContain("Muster");
        json.ShouldNotContain("1980");
        json.ShouldNotContain("Birthdate");
    }

    [Test]
    public async Task Handle_HitInAForeignGroup_CarriesNoId()
    {
        var repository = Substitute.For<IClientSearchRepository>();
        var own = new Client { Id = Guid.NewGuid(), Name = "Steiner", FirstName = "Petra" };
        var foreign = new Client { Id = Guid.NewGuid(), Name = "Steinmann", FirstName = "Petra" };
        repository.FindList(null, "Stein", null).Returns([own, foreign]);
        var handler = new FindListQueryHandler(repository, TestGroupWriteVisibility.ClientsHidden(foreign.Id));

        var result = (await handler.Handle(new FindListQuery(null, "Stein"), CancellationToken.None)).ToList();

        result.Count.ShouldBe(2);
        result.Single(r => r.Name == "Steiner").Id.ShouldBe(own.Id);
        result.Single(r => r.Name == "Steinmann").Id.ShouldBeNull();
        JsonSerializer.Serialize(result).ShouldNotContain(foreign.Id.ToString());
    }
}
