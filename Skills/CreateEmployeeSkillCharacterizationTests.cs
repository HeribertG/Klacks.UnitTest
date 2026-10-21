// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for CreateEmployeeSkill.ExecuteAsync, written before its long body was split into
/// private steps: every refusal text, the success and customer-reuse texts and serialized result data, the
/// posted client resource, and the order in which the company clock, country resolver, post-code lookup,
/// customer-reuse lookup, confirmation store and self-API are consulted.
/// </summary>

using System.Net;
using System.Text.Json;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.Infrastructure.SelfApi;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CreateEmployeeSkillCharacterizationTests
{
    private const string ClientsRoute = "api/backend/Clients";

    private static readonly Guid PersistedId = Guid.Parse("aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa");
    private static readonly Guid ExistingCustomerId = Guid.Parse("bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb");

    private IClientRepository _clientRepository = null!;
    private IClientSearchRepository _searchRepository = null!;
    private ICountryResolver _countryResolver = null!;
    private IPendingConfirmationStore _confirmationStore = null!;
    private ICompanyClock _companyClock = null!;
    private FakeSelfApi _api = null!;
    private CreateEmployeeSkill _skill = null!;
    private List<string> _calls = null!;
    private Client? _reuseCandidate;

    [SetUp]
    public void Setup()
    {
        _calls = [];
        _reuseCandidate = null;
        _clientRepository = Substitute.For<IClientRepository>();
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _countryResolver = Substitute.For<ICountryResolver>();
        _confirmationStore = Substitute.For<IPendingConfirmationStore>();
        _companyClock = Substitute.For<ICompanyClock>();

        var ch = new Countries { Abbreviation = "CH", Prefix = "+41", Name = new MultiLanguage { De = "Schweiz" } };
        _countryResolver.ResolveAsync(Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _calls.Add($"country-resolve:{ci.Arg<string?>()}");
                return ci.Arg<string?>() == "CH" ? ch : null;
            });
        _countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("country-default"); return ch; });
        _searchRepository.FindStatePostCode(Arg.Any<string>())
            .Returns(ci => { _calls.Add($"state-lookup:{ci.Arg<string>()}"); return "BE"; });
        _clientRepository.FindReusableCustomerAsync(Arg.Any<Client>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _calls.Add("reuse-lookup");
                _reuseCandidate = ci.Arg<Client>();
                return (Client?)null;
            });
        _confirmationStore
            .Create(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>())
            .Returns(_ => { _calls.Add("confirmation"); return "confirm-token"; });
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>())
            .Returns(_ => { _calls.Add("today"); return new DateTime(2026, 5, 29, 0, 0, 0, DateTimeKind.Utc); });

        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Post, ClientsRoute, new ClientResource { Id = PersistedId });

        _skill = new CreateEmployeeSkill(
            _clientRepository, _searchRepository, new ClientMapper(), _api.Client, new SelfApiRouteResolver(),
            _countryResolver, _confirmationStore, _companyClock);
    }

    [TearDown]
    public void TearDown() => _api.Dispose();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanCreateClients" },
        AccessToken = new BearerToken("caller-jwt")
    };

    private static Dictionary<string, object> Complete() => new()
    {
        ["firstName"] = "Heribert",
        ["lastName"] = "Gasparoli",
        ["gender"] = "Male",
        ["memberSince"] = "2026-05-29",
        ["street"] = "Bahnhofstrasse 1",
        ["zip"] = "3097",
        ["city"] = "Liebefeld",
        ["email"] = "heribert@example.com",
        ["phone"] = "+41 79 123 45 67"
    };

    private static Dictionary<string, object> With(params (string Key, object Value)[] overrides)
    {
        var parameters = Complete();
        foreach (var (key, value) in overrides)
        {
            parameters[key] = value;
        }

        return parameters;
    }

    private static Dictionary<string, object> Without(params string[] keys)
    {
        var parameters = Complete();
        foreach (var key in keys)
        {
            parameters.Remove(key);
        }

        return parameters;
    }

    private static Dictionary<string, object> Customer() => new()
    {
        ["firstName"] = "Biel",
        ["lastName"] = "GmbH",
        ["gender"] = "LegalEntity",
        ["company"] = "Biel GmbH",
        ["entityType"] = "Customer",
        ["memberSince"] = "2026-06-01",
        ["street"] = "Bahnhofstr 100",
        ["zip"] = "2500",
        ["city"] = "Biel",
        ["country"] = "CH",
        ["proceedWithoutContact"] = true
    };

    private const string MissingMemberSinceMessage =
        "Cannot create the client yet. Please ask the user when the employee starts working " +
        "(memberSince / Eintrittsdatum / start date), then call create_employee again with that value in YYYY-MM-DD format. " +
        "When you ask for this date, append a date-picker block so the user can pick it instead of typing: [REPLIES:date \"Ab wann?\"]. " +
        "Resolve casual phrases the user already gave you: \"heute\"/\"sofort\"/\"jetzt\"/\"direkt\"/\"today\"/\"now\" → today's date; " +
        "\"morgen\"/\"tomorrow\" → tomorrow's date; \"1. Juli\"/\"July 1st\" → the matching YYYY-MM-DD.";

    private const string IncompleteAddressMessage =
        "Cannot create the client yet. The address is incomplete: street, zip (postal code) and city are all " +
        "required when an address is given. The zip/postal code is missing — ask the user for it (e.g. 2500 for Biel) " +
        "or supply the one you already know, then call create_employee again with the complete address.";

    private static string MissingContactMessage(string missing) =>
        "Cannot create the client yet. The following onboarding data is still missing: " + missing +
        ". Ask the user for each missing item, then call create_employee again with all collected fields. " +
        "Only if the user explicitly declines to provide them, call create_employee again with proceedWithoutContact set to true.";

    [Test]
    public async Task Employee_ExactMessageDataBodyAndCallOrder()
    {
        var result = await _skill.ExecuteAsync(Ctx(), With(("birthdate", "1980-03-04"), ("company", "Klacks AG")));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            "Employee Heribert Gasparoli from BE was successfully created (with membership, email, phone) " +
            "and confirmed in the database (verified).");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"EmployeeId\":\"" + PersistedId + "\",\"FirstName\":\"Heribert\",\"LastName\":\"Gasparoli\"," +
            "\"Gender\":\"Male\",\"EntityType\":\"Employee\",\"HasMembership\":true,\"HasEmail\":true,\"HasPhone\":true," +
            "\"State\":\"BE\",\"City\":\"Liebefeld\",\"Country\":\"CH\"}");
        _calls.ShouldBe(["today", "country-resolve:", "country-default", "state-lookup:3097", "today"]);
        await _clientRepository.DidNotReceive().FindReusableCustomerAsync(Arg.Any<Client>(), Arg.Any<CancellationToken>());

        _api.SingleCall.Method.ShouldBe(HttpMethod.Post);
        _api.SingleCall.Route.ShouldBe(ClientsRoute);
        _api.SingleCall.SkillName.ShouldBe("create_employee");
        var body = _api.BodyOf<ClientResource>()!;
        body.Id.ShouldNotBe(Guid.Empty);
        body.Id.ShouldNotBe(PersistedId);
        body.FirstName.ShouldBe("Heribert");
        body.Name.ShouldBe("Gasparoli");
        body.Gender.ShouldBe(GenderEnum.Male);
        body.Company.ShouldBe("Klacks AG");
        body.Type.ShouldBe((int)EntityTypeEnum.Employee);
        body.LegalEntity.ShouldBeFalse();
        body.IdNumber.ShouldBe(0);
        body.Birthdate.ShouldBe(new DateTime(1980, 3, 4, 0, 0, 0, DateTimeKind.Utc));
        body.Membership.ShouldNotBeNull();
        body.Membership!.ValidFrom.ShouldBe(new DateTime(2026, 5, 29, 0, 0, 0, DateTimeKind.Utc));
        var address = body.Addresses.Single();
        address.Street.ShouldBe("Bahnhofstrasse 1");
        address.Zip.ShouldBe("3097");
        address.City.ShouldBe("Liebefeld");
        address.State.ShouldBe("BE");
        address.Country.ShouldBe("CH");
        address.Type.ShouldBe(AddressTypeEnum.Employee);
        body.Communications.Count.ShouldBe(2);
        var mail = body.Communications.First();
        mail.Type.ShouldBe(CommunicationTypeEnum.PrivateMail);
        mail.Value.ShouldBe("heribert@example.com");
        var phone = body.Communications.Last();
        phone.Type.ShouldBe(CommunicationTypeEnum.PrivateCellPhone);
        phone.Prefix.ShouldBe("+41");
        phone.Value.ShouldBe("791234567");
    }

    [Test]
    public async Task GivenState_SkipsThePostCodeLookup_AndExplicitCountryAvoidsTheDefault()
    {
        var result = await _skill.ExecuteAsync(Ctx(), With(("state", "ZH"), ("country", "CH")));

        result.Message.ShouldBe(
            "Employee Heribert Gasparoli from ZH was successfully created (with membership, email, phone) " +
            "and confirmed in the database (verified).");
        _calls.ShouldBe(["today", "country-resolve:CH", "today"]);
    }

    [Test]
    public async Task WithoutContact_MessageHasNoEmailOrPhone_AndDataFlagsAreFalse()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["firstName"] = "Anna",
            ["lastName"] = "Muster",
            ["gender"] = "Female",
            ["entityType"] = "ExternEmp",
            ["memberSince"] = "2026-05-29",
            ["proceedWithoutContact"] = true
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            "ExternEmp Anna Muster was successfully created (with membership) and confirmed in the database (verified).");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"EmployeeId\":\"" + PersistedId + "\",\"FirstName\":\"Anna\",\"LastName\":\"Muster\"," +
            "\"Gender\":\"Female\",\"EntityType\":\"ExternEmp\",\"HasMembership\":true,\"HasEmail\":false,\"HasPhone\":false," +
            "\"State\":null,\"City\":null,\"Country\":\"CH\"}");
        var body = _api.BodyOf<ClientResource>()!;
        body.Addresses.ShouldBeEmpty();
        body.Communications.ShouldBeEmpty();
        _calls.ShouldBe(["today", "country-resolve:", "country-default", "today"]);
    }

    [Test]
    public async Task UnresolvableCountry_WithoutDefault_LeavesTheCountryCodeEmpty()
    {
        _countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns((Countries?)null);

        var result = await _skill.ExecuteAsync(Ctx(), With(("phone", "0791234567")));

        result.Success.ShouldBeTrue(result.Message);
        JsonSerializer.Serialize(result.Data).ShouldEndWith("\"Country\":\"\"}");
        _api.BodyOf<ClientResource>()!.Addresses.Single().Country.ShouldBe(string.Empty);
    }

    [Test]
    public async Task InvalidEnums_HaveTheirExactTexts_AndAreCheckedBeforeEverythingElse()
    {
        (await _skill.ExecuteAsync(Ctx(), With(("gender", "robot"), ("memberSince", ""))))
            .Message.ShouldBe("Invalid gender value: robot");
        (await _skill.ExecuteAsync(Ctx(), With(("entityType", "Supplier"), ("memberSince", ""))))
            .Message.ShouldBe("Invalid entityType value: Supplier. Valid values: Employee, ExternEmp, Customer.");
        _calls.ShouldBeEmpty();
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task MemberSince_MissingAndInvalid_HaveTheirExactTexts()
    {
        (await _skill.ExecuteAsync(Ctx(), Without("memberSince"))).Message.ShouldBe(MissingMemberSinceMessage);
        (await _skill.ExecuteAsync(Ctx(), With(("memberSince", " ")))).Message.ShouldBe(MissingMemberSinceMessage);
        _calls.ShouldBeEmpty();

        (await _skill.ExecuteAsync(Ctx(), With(("memberSince", "irgendwann"))))
            .Message.ShouldBe(SkillDateParser.InvalidDateMessageFor("memberSince", "irgendwann"));
        _calls.ShouldBe(["today"]);
        _api.Calls.ShouldBeEmpty();
    }

    [TestCase("street")]
    [TestCase("zip")]
    [TestCase("city")]
    public async Task IncompleteAddress_IsRefused_EvenWhenContactIsWaived(string missingPart)
    {
        var parameters = Without(missingPart);
        parameters["proceedWithoutContact"] = true;

        (await _skill.ExecuteAsync(Ctx(), parameters)).Message.ShouldBe(IncompleteAddressMessage);
        _calls.ShouldBe(["today"]);
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task MissingContact_ListsExactlyTheMissingItemsInOrder()
    {
        (await _skill.ExecuteAsync(Ctx(), Without("street", "zip", "city", "email", "phone")))
            .Message.ShouldBe(MissingContactMessage("address (street, zip, city), email, phone"));
        (await _skill.ExecuteAsync(Ctx(), Without("email")))
            .Message.ShouldBe(MissingContactMessage("email"));
        (await _skill.ExecuteAsync(Ctx(), With(("phone", "  "))))
            .Message.ShouldBe(MissingContactMessage("phone"));
        _calls.ShouldBe(["today", "today", "today"]);
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task InvalidBirthdate_IsRefused_AfterCountryAndStateWereResolved()
    {
        var result = await _skill.ExecuteAsync(Ctx(), With(("birthdate", "irgendwann")));

        result.Message.ShouldBe(SkillDateParser.InvalidBirthdateMessage("irgendwann"));
        _calls.ShouldBe(["today", "country-resolve:", "country-default", "state-lookup:3097"]);
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task Customer_NotReusable_IsLookedUpWithTheBuiltClient_ThenPosted()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Customer());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe(
            "Customer Biel GmbH from BE was successfully created (with membership) and confirmed in the database (verified).");
        _calls.ShouldBe(["today", "country-resolve:CH", "state-lookup:2500", "reuse-lookup", "today"]);
        _api.Calls.Count.ShouldBe(1);

        var candidate = _reuseCandidate!;
        candidate.Company.ShouldBe("Biel GmbH");
        candidate.Type.ShouldBe(EntityTypeEnum.Customer);
        candidate.LegalEntity.ShouldBeTrue();
        candidate.CurrentUserCreated.ShouldBe("tester");
        candidate.Membership.ShouldNotBeNull();
        candidate.Addresses.Single().Zip.ShouldBe("2500");
        candidate.Addresses.Single().Street.ShouldBe("Bahnhofstr 100");
        candidate.Addresses.Single().State.ShouldBe("BE");
        candidate.Communications.ShouldBeEmpty();
    }

    [Test]
    public async Task Customer_Reusable_ExactMessageAndData_NothingPostedOrConfirmed()
    {
        _clientRepository.FindReusableCustomerAsync(Arg.Any<Client>(), Arg.Any<CancellationToken>())
            .Returns(_ =>
            {
                _calls.Add("reuse-lookup");
                return new Client { Id = ExistingCustomerId, FirstName = "Biel", Name = "GmbH", Company = "Biel GmbH" };
            });

        var parameters = Customer();
        parameters["memberSince"] = "1900-01-01";
        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe(
            "Customer 'Biel GmbH' already exists at Bahnhofstr 100, 2500 — reusing it, no duplicate created. " +
            "Use this customer id (clientId) for the order with create_shift; do NOT create another customer.");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"EmployeeId\":\"" + ExistingCustomerId + "\",\"ClientId\":\"" + ExistingCustomerId + "\"," +
            "\"FirstName\":\"Biel\",\"LastName\":\"GmbH\",\"Company\":\"Biel GmbH\",\"EntityType\":\"Customer\",\"Reused\":true}");
        _calls.ShouldBe(["today", "country-resolve:CH", "state-lookup:2500", "reuse-lookup"]);
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task Customer_ReusableWithoutCompany_IsLabelledByItsName()
    {
        _clientRepository.FindReusableCustomerAsync(Arg.Any<Client>(), Arg.Any<CancellationToken>())
            .Returns(new Client { Id = ExistingCustomerId, FirstName = "Hans", Name = "Muster", Company = " " });

        var result = await _skill.ExecuteAsync(Ctx(), Customer());

        result.Message!.ShouldStartWith("Customer 'Hans Muster' already exists at Bahnhofstr 100, 2500 — reusing it");
    }

    [Test]
    public async Task ImplausibleValidFrom_AsksForConfirmation_AfterTheReuseLookup_WithoutPosting()
    {
        var parameters = Customer();
        parameters["memberSince"] = "1900-01-01";

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        _calls.ShouldBe(["today", "country-resolve:CH", "state-lookup:2500", "reuse-lookup", "today", "confirmation"]);
        _confirmationStore.Received(1).Create(
            Arg.Any<Guid>(), "create_employee", Arg.Any<IReadOnlyDictionary<string, object>>());
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task ConfirmedValidFrom_ReadsTheCompanyDayOnlyOnce()
    {
        var result = await _skill.ExecuteAsync(Ctx(), With(("memberSince", "1900-01-01"), ("validFromPlausibilityConfirmed", true)));

        result.Success.ShouldBeTrue(result.Message);
        _calls.ShouldBe(["today", "country-resolve:", "country-default", "state-lookup:3097"]);
        await _companyClock.Received(1).GetTodayAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FailedPost_ReturnsTheSelfApiError()
    {
        _api.Respond(HttpMethod.Post, ClientsRoute, null, HttpStatusCode.Forbidden);

        var result = await _skill.ExecuteAsync(Ctx(), Complete());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Permission denied: your role is not allowed to perform this action.");
    }

    [Test]
    public async Task EmptyPostResponse_IsReportedAsUnconfirmed()
    {
        _api.Respond(HttpMethod.Post, ClientsRoute, null);

        var result = await _skill.ExecuteAsync(Ctx(), Complete());

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "Creating the Employee 'Heribert Gasparoli' returned no result — the operation may have failed.");
    }

    [Test]
    public async Task WhitespaceOnlyContactValues_PassTheGuardWhenWaived_AndArePersisted()
    {
        var result = await _skill.ExecuteAsync(Ctx(), With(
            ("email", " "),
            ("proceedWithoutContact", true)));

        result.Success.ShouldBeTrue(result.Message);
        result.Message!.ShouldContain("(with membership, email, phone)");
        _api.BodyOf<ClientResource>()!.Communications
            .Single(c => c.Type == CommunicationTypeEnum.PrivateMail).Value.ShouldBe(" ");
    }

    [Test]
    public async Task MissingRequiredParameters_ThrowInDeclarationOrder()
    {
        (await Should.ThrowAsync<ArgumentException>(() => _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>())))
            .Message.ShouldBe("Required parameter 'firstName' is missing");
        (await Should.ThrowAsync<ArgumentException>(() => _skill.ExecuteAsync(Ctx(), Without("lastName", "gender"))))
            .Message.ShouldBe("Required parameter 'lastName' is missing");
        _calls.ShouldBeEmpty();
    }
}
