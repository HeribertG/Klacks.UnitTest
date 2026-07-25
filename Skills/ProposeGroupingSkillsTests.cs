// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ProposeCustomerGroupingSkill and ProposeEmployeeGroupingSkill: the dry run must state
/// how many existing memberships would end and name them per client, because a move can end further
/// memberships and the user confirms the proposal, not the applied result. The user-facing texts must
/// stay free of internal skill names.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Queries.Grouping;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ProposeGroupingSkillsTests
{
    private const string MatchReason = "nearest coordinates (main address)";

    private static readonly string[] InternalSkillNames =
    {
        "set_group_location",
        "geocode_location_groups",
        "apply_customer_grouping",
        "apply_employee_grouping",
        "propose_customer_grouping",
        "propose_employee_grouping"
    };

    private static readonly Guid CityId = Guid.NewGuid();
    private static readonly Guid CantonId = Guid.NewGuid();
    private static readonly Guid RegionId = Guid.NewGuid();

    private IMediator _mediator = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
    }

    [Test]
    public async Task CustomerProposal_ReportsHowManyMembershipsWouldEnd()
    {
        SetProposal(
            Assignment("Anna Meier", new[] { CantonId, RegionId }, new[] { "ZH", "Ostschweiz" }),
            Assignment("Bea Huber", new[] { CantonId }, new[] { "ZH" }));

        var result = await new ProposeCustomerGroupingSkill(_mediator).ExecuteAsync(Ctx(), NoParameters());

        var json = JsonSerializer.Serialize(result.Data);
        json.ShouldContain("\"MembershipsToEnd\":3");
        json.ShouldContain("\"CustomersLosingAMembership\":2");
        result.Message.ShouldContain("3 existing group membership(s) of 2 customer(s)");
    }

    [Test]
    public async Task CustomerProposal_NamesTheMembershipsThatWouldEnd_PerSampleRow()
    {
        SetProposal(Assignment("Anna Meier", new[] { CantonId, RegionId }, new[] { "ZH", "Ostschweiz" }));

        var result = await new ProposeCustomerGroupingSkill(_mediator).ExecuteAsync(Ctx(), NoParameters());

        var json = JsonSerializer.Serialize(result.Data);
        json.ShouldContain("\"Ends\":\"ZH/Ostschweiz\"");
    }

    [Test]
    public async Task CustomerProposal_WithoutRetiredMemberships_ReportsZero()
    {
        SetProposal(Assignment("Cara Frei", Array.Empty<Guid>(), Array.Empty<string>()));

        var result = await new ProposeCustomerGroupingSkill(_mediator).ExecuteAsync(Ctx(), NoParameters());

        var json = JsonSerializer.Serialize(result.Data);
        json.ShouldContain("\"MembershipsToEnd\":0");
        json.ShouldContain("\"Ends\":\"(none)\"");
    }

    [Test]
    public async Task EmployeeProposal_ReportsHowManyMembershipsWouldEnd()
    {
        SetProposal(Assignment("Gina Vogel", new[] { CantonId }, new[] { "ZH" }));

        var result = await new ProposeEmployeeGroupingSkill(_mediator).ExecuteAsync(Ctx(), NoParameters());

        var json = JsonSerializer.Serialize(result.Data);
        json.ShouldContain("\"MembershipsToEnd\":1");
        json.ShouldContain("\"EmployeesLosingAMembership\":1");
        result.Message.ShouldContain("1 existing group membership(s) of 1 employee(s)");
    }

    [Test]
    public async Task Proposals_DoNotLeakInternalSkillNames()
    {
        SetProposal(Assignment("Anna Meier", new[] { CantonId }, new[] { "ZH" }));

        var customerMessage = (await new ProposeCustomerGroupingSkill(_mediator)
            .ExecuteAsync(Ctx(), NoParameters())).Message;
        var employeeMessage = (await new ProposeEmployeeGroupingSkill(_mediator)
            .ExecuteAsync(Ctx(), NoParameters())).Message;

        foreach (var message in new[] { customerMessage, employeeMessage })
        {
            foreach (var skillName in InternalSkillNames)
            {
                message.ShouldNotContain(skillName);
            }
        }
    }

    [Test]
    public async Task Proposals_WithoutAnchors_DoNotLeakInternalSkillNames()
    {
        SetProposal(0);

        var customerMessage = (await new ProposeCustomerGroupingSkill(_mediator)
            .ExecuteAsync(Ctx(), NoParameters())).Message;
        var employeeMessage = (await new ProposeEmployeeGroupingSkill(_mediator)
            .ExecuteAsync(Ctx(), NoParameters())).Message;

        foreach (var message in new[] { customerMessage, employeeMessage })
        {
            foreach (var skillName in InternalSkillNames)
            {
                message.ShouldNotContain(skillName);
            }
        }
    }

    private void SetProposal(params CustomerGroupingAssignment[] assignments) => SetProposal(2, assignments);

    private void SetProposal(int anchorGroupCount, params CustomerGroupingAssignment[] assignments)
    {
        _mediator.Send(Arg.Any<ProposeCustomerGroupingQuery>(), Arg.Any<CancellationToken>())
            .Returns(new CustomerGroupingProposal(
                anchorGroupCount,
                assignments,
                Array.Empty<UnassignedCustomer>()));
    }

    private static CustomerGroupingAssignment Assignment(
        string clientName, IReadOnlyList<Guid> retireGroupIds, IReadOnlyList<string> retireGroupNames)
        => new(
            Guid.NewGuid(),
            clientName,
            retireGroupNames,
            CityId,
            "Zürich",
            3.2,
            retireGroupIds,
            retireGroupNames,
            MatchReason);

    private static Dictionary<string, object> NoParameters() => new();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients", "CanViewGroups" }
    };
}
