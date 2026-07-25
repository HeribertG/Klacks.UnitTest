// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ApplyGroupingSkill, focused on the membership start date. A date the user named must
/// reach the command unchanged, an omitted one must stay null so the handler can fall back to the company
/// date, and an unparseable one must be rejected instead of silently becoming today — a wrong membership
/// start is invisible in the answer and would be worse than a visible error. The reported date is the one
/// the handler actually wrote, not the one that was requested.
/// </summary>

using Klacks.Api.Application.Commands.Grouping;
using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ApplyGroupingSkillTests
{
    private static readonly DateTime RequestedValidFrom = new(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CompanyToday = new(2026, 7, 25, 0, 0, 0, DateTimeKind.Utc);

    private IMediator _mediator = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        SetResult(CompanyToday);
    }

    [Test]
    public async Task Apply_WithExplicitValidFrom_PassesItToTheCommandAsUtc()
    {
        SetResult(RequestedValidFrom);

        var result = await Execute(Parameters("Customer", "2026-06-01"));

        result.Success.ShouldBeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<ApplyCustomerGroupingCommand>(c =>
                c.EntityType == EntityTypeEnum.Customer
                && c.ValidFrom == RequestedValidFrom
                && c.ValidFrom!.Value.Kind == DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Apply_WithoutValidFrom_LeavesItNullSoTheHandlerDecides()
    {
        var result = await Execute(Parameters("Customer"));

        result.Success.ShouldBeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<ApplyCustomerGroupingCommand>(c => c.ValidFrom == null),
            Arg.Any<CancellationToken>());
    }

    [TestCase("1. Juni 2026")]
    [TestCase("01.06.2026")]
    [TestCase("06/01/2026")]
    [TestCase("today")]
    [TestCase("2026-13-45")]
    public async Task Apply_WithUnparseableValidFrom_FailsInsteadOfSilentlyUsingToday(string validFrom)
    {
        var result = await Execute(Parameters("Customer", validFrom));

        result.Success.ShouldBeFalse();
        await _mediator.DidNotReceive().Send(
            Arg.Any<ApplyCustomerGroupingCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Apply_ReportsTheDateTheHandlerActuallyWrote()
    {
        SetResult(CompanyToday);

        var result = await Execute(Parameters("Customer"));

        result.Message.ShouldContain("2026-07-25");
    }

    [Test]
    public async Task Apply_WithInvalidEntityType_ReturnsError()
    {
        var result = await Execute(Parameters("banana"));

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task Apply_DoesNotLeakInternalSkillNames()
    {
        var result = await Execute(Parameters("Customer"));

        result.Message.ShouldNotContain("apply_grouping");
        result.Message.ShouldNotContain("propose_grouping");
        result.Message.ShouldNotContain("add_client_to_group");
    }

    private Task<SkillResult> Execute(Dictionary<string, object> parameters) =>
        new ApplyGroupingSkill(_mediator).ExecuteAsync(Ctx(), parameters);

    private void SetResult(DateTime appliedValidFrom)
    {
        _mediator.Send(Arg.Any<ApplyCustomerGroupingCommand>(), Arg.Any<CancellationToken>())
            .Returns(new CustomerGroupingApplyResult(2, 2, 0, 1, appliedValidFrom));
    }

    private static Dictionary<string, object> Parameters(string entityType, string? validFrom = null)
    {
        var parameters = new Dictionary<string, object> { ["entityType"] = entityType };
        if (validFrom != null)
        {
            parameters["validFrom"] = validFrom;
        }

        return parameters;
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients", "CanViewGroups" }
    };
}
