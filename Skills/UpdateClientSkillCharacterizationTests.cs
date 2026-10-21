// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for UpdateClientSkill.ExecuteAsync, written before its long body was split into
/// private steps: every refusal text, the success/no-op texts and serialized result data, the field-by-field
/// mutation of the loaded client (including the fields already applied when a later field is rejected), the
/// address/communication entries added with one shared timestamp, and the order of visibility guard, repository,
/// country resolver and self-API PUT.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateClientSkillCharacterizationTests
{
    private static readonly Guid ClientId = Guid.Parse("11111111-1111-1111-1111-111111111111");

    private IClientRepository _clientRepository = null!;
    private IClientVisibilityGuard _visibilityGuard = null!;
    private IClientSearchRepository _searchRepository = null!;
    private IKlacksSelfApiClient _selfApi = null!;
    private ILogger<UpdateClientSkill> _logger = null!;
    private ICountryResolver _countryResolver = null!;
    private UpdateClientSkill _skill = null!;
    private List<string> _calls = null!;
    private Client _existing = null!;
    private SelfApiResult<ClientResource> _putResult = null!;
    private string? _putRoute;
    private object? _putBody;
    private string? _putSkillName;
    private Countries? _resolvedCountry;
    private Countries? _defaultCountry;

    [SetUp]
    public void Setup()
    {
        _calls = [];
        _putRoute = null;
        _putBody = null;
        _putSkillName = null;
        _resolvedCountry = new Countries { Abbreviation = "CH" };
        _defaultCountry = new Countries { Abbreviation = "DE" };
        _putResult = SelfApiResult<ClientResource>.Ok(new ClientResource(), 200);
        _existing = new Client
        {
            Id = ClientId,
            FirstName = "Old",
            Name = "Muster",
            Title = "Prof.",
            Company = "Old AG",
            Birthdate = new DateTime(1970, 1, 1, 0, 0, 0, DateTimeKind.Utc),
            Gender = GenderEnum.Female,
            Type = EntityTypeEnum.Employee
        };

        _clientRepository = Substitute.For<IClientRepository>();
        _visibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _selfApi = Substitute.For<IKlacksSelfApiClient>();
        _logger = Substitute.For<ILogger<UpdateClientSkill>>();
        _countryResolver = Substitute.For<ICountryResolver>();

        _visibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("visible"); return true; });
        _clientRepository.Get(Arg.Any<Guid>())
            .Returns(_ => { _calls.Add("get"); return _existing; });
        _countryResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci => { _calls.Add($"country-resolve:{ci.Arg<string?>()}"); return _resolvedCountry; });
        _countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("country-default"); return _defaultCountry; });
        _selfApi.PutAsync<ClientResource>(
                Arg.Any<string>(), Arg.Any<object>(), Arg.Any<SkillExecutionContext>(), Arg.Any<string>(),
                Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _calls.Add("put");
                _putRoute = ci.ArgAt<string>(0);
                _putBody = ci.ArgAt<object>(1);
                _putSkillName = ci.ArgAt<string>(3);
                return _putResult;
            });

        _skill = new UpdateClientSkill(
            _clientRepository, _visibilityGuard, _searchRepository, new ClientMapper(), _selfApi,
            new SelfApiRouteResolver(), _logger, _countryResolver);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    private static Dictionary<string, object> Params(params (string Key, object Value)[] values)
    {
        var parameters = new Dictionary<string, object> { ["clientId"] = ClientId.ToString() };
        foreach (var (key, value) in values)
        {
            parameters[key] = value;
        }

        return parameters;
    }

    [Test]
    public async Task AllFields_ExactMessageDataMutationsAndCallOrder()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("firstName", "Anna"),
            ("lastName", "Mueller"),
            ("title", "Dr."),
            ("company", "Acme"),
            ("birthdate", "1984-05-01"),
            ("gender", "male"),
            ("street", "Hauptstrasse 1"),
            ("zip", "3000"),
            ("city", "Bern"),
            ("state", "BE"),
            ("country", "Schweiz"),
            ("email", "anna@example.ch"),
            ("phone", "079 123 45 67")));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            "Client 'Anna Mueller' updated (firstName, lastName, title, company, birthdate, gender, address, email, phone).");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"ClientId\":\"" + ClientId + "\",\"ChangedFields\":[\"firstName\",\"lastName\",\"title\",\"company\"," +
            "\"birthdate\",\"gender\",\"address\",\"email\",\"phone\"],\"FirstName\":\"Anna\",\"LastName\":\"Mueller\"," +
            "\"EntityType\":\"Employee\"}");
        _calls.ShouldBe(["visible", "get", "country-resolve:Schweiz", "put"]);
        _putRoute.ShouldBe("api/backend/Clients");
        _putSkillName.ShouldBe("update_client");
        var body = _putBody.ShouldBeOfType<ClientResource>();
        body.Id.ShouldBe(ClientId);
        body.FirstName.ShouldBe("Anna");
        body.Name.ShouldBe("Mueller");

        _existing.FirstName.ShouldBe("Anna");
        _existing.Name.ShouldBe("Mueller");
        _existing.Title.ShouldBe("Dr.");
        _existing.Company.ShouldBe("Acme");
        _existing.Birthdate.ShouldBe(new DateTime(1984, 5, 1, 0, 0, 0, DateTimeKind.Utc));
        _existing.Gender.ShouldBe(GenderEnum.Male);
        _existing.LegalEntity.ShouldBeFalse();

        var address = _existing.Addresses.Single();
        address.Id.ShouldNotBe(Guid.Empty);
        address.ClientId.ShouldBe(ClientId);
        address.Street.ShouldBe("Hauptstrasse 1");
        address.Zip.ShouldBe("3000");
        address.City.ShouldBe("Bern");
        address.State.ShouldBe("BE");
        address.Country.ShouldBe("CH");
        address.Type.ShouldBe(AddressTypeEnum.Employee);
        address.CurrentUserCreated.ShouldBe("tester");
        address.ValidFrom.ShouldNotBeNull();
        address.CreateTime.ShouldBe(address.ValidFrom);

        var communications = _existing.Communications.ToList();
        communications.Count.ShouldBe(2);
        communications[0].Type.ShouldBe(CommunicationTypeEnum.PrivateMail);
        communications[0].Value.ShouldBe("anna@example.ch");
        communications[1].Type.ShouldBe(CommunicationTypeEnum.PrivateCellPhone);
        communications[1].Value.ShouldBe("079 123 45 67");
        communications.ShouldAllBe(c => c.ClientId == ClientId && c.CurrentUserCreated == "tester");
        communications.ShouldAllBe(c => c.CreateTime == address.ValidFrom);
        new[] { address.Id, communications[0].Id, communications[1].Id }.Distinct().Count().ShouldBe(3);
    }

    [Test]
    public async Task CustomerAddress_IsWorkplace_FallsBackToTheDefaultCountry_BlankPartsBecomeEmpty()
    {
        _existing.Type = EntityTypeEnum.Customer;
        _resolvedCountry = null;

        var result = await _skill.ExecuteAsync(Ctx(), Params(("zip", "8000")));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe("Client 'Old Muster' updated (address).");
        JsonSerializer.Serialize(result.Data).ShouldEndWith("\"EntityType\":\"Customer\"}");
        _calls.ShouldBe(["visible", "get", "country-resolve:", "country-default", "put"]);
        var address = _existing.Addresses.Single();
        address.Type.ShouldBe(AddressTypeEnum.Workplace);
        address.Street.ShouldBe(string.Empty);
        address.City.ShouldBe(string.Empty);
        address.State.ShouldBe(string.Empty);
        address.Country.ShouldBe("DE");
    }

    [Test]
    public async Task AddressWithoutAnyKnownCountry_GetsAnEmptyCountryCode()
    {
        _resolvedCountry = null;
        _defaultCountry = null;

        await _skill.ExecuteAsync(Ctx(), Params(("city", "Bern")));

        _existing.Addresses.Single().Country.ShouldBe(string.Empty);
    }

    [Test]
    public async Task CountryOrStateAlone_AddNoAddress_AndSkipTheCountryLookup()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(("country", "CH"), ("state", "BE"), ("email", "x@y.ch")));

        result.Message.ShouldBe("Client 'Old Muster' updated (email).");
        _calls.ShouldBe(["visible", "get", "put"]);
        _existing.Addresses.ShouldBeEmpty();
    }

    [Test]
    public async Task UnchangedOrBlankValues_AreNoOp_ExactMessageAndData_NoWrite()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("firstName", "Old"),
            ("lastName", "  "),
            ("title", "Prof."),
            ("company", "Old AG"),
            ("birthdate", "1970-01-01"),
            ("gender", "FEMALE"),
            ("street", " "),
            ("email", " "),
            ("phone", "")));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe("No fields supplied for update — client left unchanged.");
        JsonSerializer.Serialize(result.Data).ShouldBe("{\"ClientId\":\"" + ClientId + "\",\"ChangedFields\":[]}");
        _calls.ShouldBe(["visible", "get"]);
    }

    [Test]
    public async Task EmptyTitleAndCompany_AreChanges()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(("title", ""), ("company", "")));

        result.Message.ShouldBe("Client 'Old Muster' updated (title, company).");
        _existing.Title.ShouldBe(string.Empty);
        _existing.Company.ShouldBe(string.Empty);
    }

    [Test]
    public async Task BirthdateError_WinsOverGenderError_AfterEarlierFieldsWereApplied()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("firstName", "Anna"),
            ("birthdate", "irgendwann"),
            ("gender", "Robot"),
            ("street", "Hauptstrasse 1")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "Invalid birthdate 'irgendwann'. Please give a concrete date (for example 1984-05-01) in ISO " +
            "format; relative words such as 'today' or 'tomorrow' are not valid birthdates.");
        _existing.FirstName.ShouldBe("Anna");
        _calls.ShouldBe(["visible", "get"]);
    }

    [Test]
    public async Task GenderError_ExactText_StopsBeforeTheAddress()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(("gender", "Robot"), ("street", "Hauptstrasse 1")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Invalid gender value: Robot");
        _calls.ShouldBe(["visible", "get"]);
        _existing.Addresses.ShouldBeEmpty();
    }

    [Test]
    public async Task LegalEntityGender_SetsTheLegalEntityFlag()
    {
        await _skill.ExecuteAsync(Ctx(), Params(("gender", "LegalEntity")));

        _existing.Gender.ShouldBe(GenderEnum.LegalEntity);
        _existing.LegalEntity.ShouldBeTrue();
    }

    [Test]
    public async Task FailedPut_PassesTheSelfApiErrorThrough_AndLogsAWarning()
    {
        _putResult = SelfApiResult<ClientResource>.Failed(400, "Validation failed: Name is required.");

        var result = await _skill.ExecuteAsync(Ctx(), Params(("firstName", "Anna")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Validation failed: Name is required.");
        _logger.ReceivedCalls()
            .Count(call => call.GetMethodInfo().Name == nameof(ILogger.Log)
                && (LogLevel)call.GetArguments()[0]! == LogLevel.Warning)
            .ShouldBe(1);
    }

    [Test]
    public async Task UnknownClientId_ExactText()
    {
        _existing = null!;
        _clientRepository.Get(Arg.Any<Guid>()).Returns((Client?)null);

        var result = await _skill.ExecuteAsync(Ctx(), Params(("firstName", "Anna")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Client with ID '{ClientId}' not found.");
    }
}
