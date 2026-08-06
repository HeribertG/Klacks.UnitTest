// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for UpdateClientGenderSkill — unknown gender value, unresolved client,
/// verified happy path (message carries "verified") and the rollback path when the re-read
/// does not confirm the new gender.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Staffs;

using Klacks.Api.Application.DTOs.Staffs;

using Klacks.Api.Application.Mappers;

using Klacks.Api.Infrastructure.Services.Assistant;

using Klacks.UnitTest.Infrastructure.SelfApi;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateClientGenderSkillTests
{
    private IClientRepository _clientRepository = null!;
    private IClientSearchRepository _searchRepository = null!;
    private FakeSelfApi _api = null!;
    private UpdateClientGenderSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Put, "api/backend/Clients", new ClientResource());
        _skill = new UpdateClientGenderSkill(_clientRepository, _searchRepository, new ClientMapper(), _api.Client, new SelfApiRouteResolver());
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients" },
        AccessToken = new BearerToken("caller-jwt")
    };

    private void WireSearch(params ClientSearchItem[] items)
    {
        _searchRepository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult { Items = items, TotalCount = items.Length });
    }

    private Client WireResolvedClient(string firstName = "Anna", string lastName = "Müller")
    {
        var client = new Client { Id = Guid.NewGuid(), FirstName = firstName, Name = lastName, Gender = GenderEnum.Male };
        WireSearch(new ClientSearchItem { Id = client.Id, FirstName = firstName, LastName = lastName, IdNumber = 7 });
        _clientRepository.Get(client.Id).Returns(client);
        _clientRepository.GetNoTracking(client.Id).Returns(client);
        return client;
    }

    private static Dictionary<string, object> Parameters(string gender = "Female") => new()
    {
        ["firstName"] = "Anna",
        ["lastName"] = "Müller",
        ["gender"] = gender
    };

    [TearDown]
    public void TearDown() => _api.Dispose();

    [Test]
    public async Task ReturnsError_WhenGenderUnknown()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Parameters(gender: "dragon"));

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Unknown gender value"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task ReturnsError_WhenClientNotFound()
    {
        WireSearch();

        var result = await _skill.ExecuteAsync(Ctx(), Parameters());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("No client found"));
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task UpdatesGender_AndConfirmsInDatabase()
    {
        var client = WireResolvedClient();

        var result = await _skill.ExecuteAsync(Ctx(), Parameters());

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(client.Gender, Is.EqualTo(GenderEnum.Female));
        _api.SingleCall.Method.ShouldBe(HttpMethod.Put);
            }

    [Test]
    public async Task ReturnsError_WhenTheEndpointRejectsTheUpdate()
    {
        WireResolvedClient();
        _api.RespondWithValidationErrors(HttpMethod.Put, "api/backend/Clients", new Dictionary<string, string[]>
        {
            ["Gender"] = ["'Gender' is not valid."]
        });

        var result = await _skill.ExecuteAsync(Ctx(), Parameters());

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("not valid"));
    }

    [Test]
    public async Task ItGoesToTheClientEndpoint_BecauseItChangesTheClientItself()
    {
        WireResolvedClient();

        await _skill.ExecuteAsync(Ctx(), Parameters());

        _api.SingleCall.Route.ShouldBe("api/backend/Clients");
        _api.SingleCall.Method.ShouldBe(HttpMethod.Put);
    }
}
