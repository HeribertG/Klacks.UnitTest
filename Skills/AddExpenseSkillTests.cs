// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for add_expense, the first skill converted to a real HTTP call against the own REST API.
/// Beyond the field mapping they pin the contract every converted skill inherits: the caller's token is
/// re-presented, the skill name rides along for the request log, validation messages from the endpoint
/// reach the user, and a call without a token is refused instead of writing past [Authorize].
/// </summary>

using System.Net;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.Infrastructure.SelfApi;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddExpenseSkillTests
{
    private static readonly Guid WorkId = Guid.NewGuid();
    private const string Token = "caller-jwt";

    private FakeSelfApi _api = null!;
    private AddExpenseSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _api = new FakeSelfApi();
        _skill = new AddExpenseSkill(_api.Client, new SelfApiRouteResolver());
    }

    [TearDown]
    public void TearDown() => _api.Dispose();

    private static SkillExecutionContext Ctx(string? token = Token) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" },
        SessionId = "conversation-1",
        AccessToken = token is null ? null : new BearerToken(token)
    };

    private static Dictionary<string, object> Params(bool full = true)
    {
        var p = new Dictionary<string, object>
        {
            ["workId"] = WorkId.ToString(),
            ["amount"] = full ? 25.50m : 10m
        };

        if (full)
        {
            p["description"] = "Taxi to client site";
            p["taxable"] = true;
        }

        return p;
    }

    private void RespondEcho(Guid? id = null) =>
        _api.Respond(HttpMethod.Post, SelfApiRoutes.Expenses, new ExpensesResource
        {
            Id = id ?? Guid.NewGuid(),
            WorkId = WorkId,
            Amount = 25.50m,
            Description = "Taxi to client site",
            Taxable = true
        });

    [Test]
    public async Task Add_PostsTheResourceToTheExpensesEndpoint()
    {
        RespondEcho();

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue();
        _api.SingleCall.Method.ShouldBe(HttpMethod.Post);
        _api.SingleCall.Route.ShouldBe(SelfApiRoutes.Expenses);

        var sent = _api.BodyOf<ExpensesResource>();
        sent.ShouldNotBeNull();
        sent!.WorkId.ShouldBe(WorkId);
        sent.Amount.ShouldBe(25.50m);
        sent.Description.ShouldBe("Taxi to client site");
        sent.Taxable.ShouldBeTrue();
    }

    [Test]
    public async Task Add_RePresentsTheCallersTokenAndNamesTheSkill()
    {
        RespondEcho();

        await _skill.ExecuteAsync(Ctx(), Params());

        _api.SingleCall.BearerToken.ShouldBe(Token);
        _api.SingleCall.SkillName.ShouldBe("add_expense");
        _api.SingleCall.CorrelationId.ShouldBe("conversation-1");
    }

    [Test]
    public async Task Add_DefaultsDescriptionAndTaxable()
    {
        _api.Respond(HttpMethod.Post, SelfApiRoutes.Expenses, new ExpensesResource
        {
            Id = Guid.NewGuid(),
            WorkId = WorkId,
            Amount = 10m
        });

        var result = await _skill.ExecuteAsync(Ctx(), Params(full: false));

        result.Success.ShouldBeTrue();
        var sent = _api.BodyOf<ExpensesResource>();
        sent!.Description.ShouldBe(string.Empty);
        sent.Taxable.ShouldBeFalse();
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
        _api.RespondWithValidationErrors(HttpMethod.Post, SelfApiRoutes.Expenses, new Dictionary<string, string[]>
        {
            ["Amount"] = ["Amount must be greater than zero."]
        });

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Amount must be greater than zero.");
    }

    [Test]
    public async Task Add_Forbidden_SaysPermissionDenied()
    {
        _api.RespondWithProblem(HttpMethod.Post, SelfApiRoutes.Expenses, HttpStatusCode.Forbidden, "nope");

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Permission denied");
    }

    [Test]
    public async Task Add_ExpiredToken_TellsTheUserToSignInAgain()
    {
        _api.RespondWithProblem(HttpMethod.Post, SelfApiRoutes.Expenses, HttpStatusCode.Unauthorized, "expired");

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Sign in again");
    }

    [Test]
    public async Task Add_EmptyBody_ReturnsError()
    {
        _api.Respond(HttpMethod.Post, SelfApiRoutes.Expenses, null, HttpStatusCode.NoContent);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("no result");
    }
}
