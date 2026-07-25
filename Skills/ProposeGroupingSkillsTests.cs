// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ProposeGroupingSkill: the dry run must state how many existing memberships would end
/// and name them per client, because a move can end further memberships and the user confirms the
/// proposal, not the applied result. The user-facing texts must stay free of internal skill names. Covers
/// all three entityType values (Employee, ExternEmp, Customer) since the skill is shared across them.
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
        "apply_grouping",
        "propose_grouping"
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

        var result = await new ProposeGroupingSkill(_mediator).ExecuteAsync(Ctx(), Parameters("Customer"));

        var json = JsonSerializer.Serialize(result.Data);
        json.ShouldContain("\"MembershipsToEnd\":3");
        json.ShouldContain("\"LosingAMembership\":2");
        result.Message.ShouldContain("3 existing group membership(s) of 2 customer(s)");
    }

    [Test]
    public async Task CustomerProposal_NamesTheMembershipsThatWouldEnd_PerSampleRow()
    {
        SetProposal(Assignment("Anna Meier", new[] { CantonId, RegionId }, new[] { "ZH", "Ostschweiz" }));

        var result = await new ProposeGroupingSkill(_mediator).ExecuteAsync(Ctx(), Parameters("Customer"));

        var json = JsonSerializer.Serialize(result.Data);
        json.ShouldContain("\"Ends\":\"ZH/Ostschweiz\"");
    }

    [Test]
    public async Task CustomerProposal_WithoutRetiredMemberships_ReportsZero()
    {
        SetProposal(Assignment("Cara Frei", Array.Empty<Guid>(), Array.Empty<string>()));

        var result = await new ProposeGroupingSkill(_mediator).ExecuteAsync(Ctx(), Parameters("Customer"));

        var json = JsonSerializer.Serialize(result.Data);
        json.ShouldContain("\"MembershipsToEnd\":0");
        json.ShouldContain("\"Ends\":\"(none)\"");
    }

    [Test]
    public async Task EmployeeProposal_ReportsHowManyMembershipsWouldEnd()
    {
        SetProposal(Assignment("Gina Vogel", new[] { CantonId }, new[] { "ZH" }));

        var result = await new ProposeGroupingSkill(_mediator).ExecuteAsync(Ctx(), Parameters("Employee"));

        var json = JsonSerializer.Serialize(result.Data);
        json.ShouldContain("\"MembershipsToEnd\":1");
        json.ShouldContain("\"LosingAMembership\":1");
        result.Message.ShouldContain("1 existing group membership(s) of 1 employee(s)");
    }

    [Test]
    public async Task ExternProposal_ReportsHowManyMembershipsWouldEnd()
    {
        SetProposal(Assignment("Ivo Keller", new[] { CantonId }, new[] { "ZH" }));

        var result = await new ProposeGroupingSkill(_mediator).ExecuteAsync(Ctx(), Parameters("ExternEmp"));

        var json = JsonSerializer.Serialize(result.Data);
        json.ShouldContain("\"MembershipsToEnd\":1");
        json.ShouldContain("\"LosingAMembership\":1");
        result.Message.ShouldContain("1 existing group membership(s) of 1 external employee(s)");
    }

    [TestCase("banana")]
    [TestCase("")]
    public async Task Proposal_WithInvalidEntityType_ReturnsError(string entityType)
    {
        var result = await new ProposeGroupingSkill(_mediator).ExecuteAsync(Ctx(), Parameters(entityType));

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task Proposals_DoNotLeakInternalSkillNames()
    {
        SetProposal(Assignment("Anna Meier", new[] { CantonId }, new[] { "ZH" }));

        foreach (var entityType in new[] { "Customer", "Employee", "ExternEmp" })
        {
            var message = (await new ProposeGroupingSkill(_mediator)
                .ExecuteAsync(Ctx(), Parameters(entityType))).Message;

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

        foreach (var entityType in new[] { "Customer", "Employee", "ExternEmp" })
        {
            var message = (await new ProposeGroupingSkill(_mediator)
                .ExecuteAsync(Ctx(), Parameters(entityType))).Message;

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

    private static Dictionary<string, object> Parameters(string entityType) => new()
    {
        ["entityType"] = entityType
    };

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients", "CanViewGroups" }
    };
}
