// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for add_client_email, the first read-modify-write skill converted to a REST call. The
/// point they pin is which endpoint it uses: the communications one, which owns exactly the row being
/// created. Going through the client endpoint would mean reading the whole client, changing one
/// collection and writing it all back — a lost-update window plus the full client-update machinery for
/// a single insert. Resolving the client by name stays a direct read; only the write moves to HTTP.
/// </summary>

using System.Net;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.Infrastructure.SelfApi;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddClientEmailSkillTests
{
    private const string Route = "api/backend/Communications";
    private const string Email = "peter@example.com";

    private static readonly Guid ClientId = Guid.NewGuid();

    private FakeSelfApi _api = null!;
    private IClientRepository _clientRepository = null!;
    private IClientSearchRepository _searchRepository = null!;
    private AddClientEmailSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _api = new FakeSelfApi();
        _clientRepository = Substitute.For<IClientRepository>();
        _searchRepository = Substitute.For<IClientSearchRepository>();

        _searchRepository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                Items = [new ClientSearchItem { Id = ClientId, FirstName = "Peter", LastName = "Weiss" }],
                TotalCount = 1
            });
        _clientRepository.Get(ClientId).Returns(new Client
        {
            Id = ClientId,
            FirstName = "Peter",
            Name = "Weiss"
        });

        _skill = new AddClientEmailSkill(
            _clientRepository, _searchRepository, _api.Client, new SelfApiRouteResolver());
    }

    [TearDown]
    public void TearDown() => _api.Dispose();

    private static SkillExecutionContext Ctx(string? token = "caller-jwt") => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients" },
        SessionId = "conversation-1",
        AccessToken = token is null ? null : new BearerToken(token)
    };

    private static Dictionary<string, object> Params() => new()
    {
        ["firstName"] = "Peter",
        ["lastName"] = "Weiss",
        ["email"] = Email
    };

    private void RespondCreated() =>
        _api.Respond(HttpMethod.Post, Route, new CommunicationResource
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            Type = CommunicationTypeEnum.PrivateMail,
            Value = Email
        });

    [Test]
    public async Task Add_PostsOneCommunication_NotAWholeClient()
    {
        RespondCreated();

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        _api.SingleCall.Method.ShouldBe(HttpMethod.Post);
        _api.SingleCall.Route.ShouldBe(Route);

        var sent = _api.BodyOf<CommunicationResource>();
        sent.ShouldNotBeNull();
        sent!.ClientId.ShouldBe(ClientId);
        sent.Value.ShouldBe(Email);
        sent.Type.ShouldBe(CommunicationTypeEnum.PrivateMail);
    }

    [Test]
    public async Task Add_NeverReadsOrWritesTheClientOverHttp()
    {
        RespondCreated();

        await _skill.ExecuteAsync(Ctx(), Params());

        _api.Calls.ShouldAllBe(call => !call.Route.Contains("Clients"));
    }

    [Test]
    public async Task Add_RePresentsTheCallersTokenAndNamesTheSkill()
    {
        RespondCreated();

        await _skill.ExecuteAsync(Ctx(), Params());

        _api.SingleCall.BearerToken.ShouldBe("caller-jwt");
        _api.SingleCall.SkillName.ShouldBe("add_client_email");
    }

    [Test]
    public async Task Add_UnknownClient_FailsBeforeSendingAnything()
    {
        _searchRepository.SearchAsync(
                Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult { Items = [], TotalCount = 0 });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No client found");
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task Add_WithoutToken_IsRefusedAndSendsNothing()
    {
        var result = await _skill.ExecuteAsync(Ctx(token: null), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("access token");
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task Add_ValidationFailure_RelaysTheFieldMessages()
    {
        _api.RespondWithValidationErrors(HttpMethod.Post, Route, new Dictionary<string, string[]>
        {
            ["Value"] = ["'Value' is not a valid email address."]
        });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not a valid email address");
    }

    [Test]
    public async Task Add_Forbidden_SaysPermissionDenied()
    {
        _api.RespondWithProblem(HttpMethod.Post, Route, HttpStatusCode.Forbidden, "nope");

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Permission denied");
    }

    [Test]
    public async Task Add_EmptyBody_ReturnsError()
    {
        _api.Respond(HttpMethod.Post, Route, null, HttpStatusCode.NoContent);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("no result");
    }
}
