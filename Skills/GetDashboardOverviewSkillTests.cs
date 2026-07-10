// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GetDashboardOverviewSkill — tree flattening with totals, largest-first
/// limiting, restricted-without-groups short circuit and the restricted note.
/// </summary>

using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Dashboard;
using Klacks.Api.Application.Queries.Dashboard;
using Klacks.Api.Application.Queries.Groups;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetDashboardOverviewSkillTests
{
    private IMediator _mediator = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    private static GroupResource Node(string name, int customers = 0, int shifts = 0, int employees = 0,
        params GroupResource[] children)
    {
        var node = new GroupResource
        {
            Id = Guid.NewGuid(),
            Name = name,
            CustomersCount = customers,
            ShiftsCount = shifts,
            EmployeesCount = employees
        };
        node.Children.AddRange(children);
        return node;
    }

    private void WireVisibility(bool restricted, bool hasGroups) =>
        _mediator.Send(Arg.Any<GetDashboardVisibilityStatusQuery>(), Arg.Any<CancellationToken>())
            .Returns(new DashboardVisibilityStatusResource { IsRestricted = restricted, HasVisibleGroups = hasGroups });

    [Test]
    public async Task AggregatesTotals_AcrossNestedGroups()
    {
        WireVisibility(restricted: false, hasGroups: true);
        _mediator.Send(Arg.Any<GetGroupTreeQuery>(), Arg.Any<CancellationToken>())
            .Returns(new GroupTreeResource
            {
                Nodes = new List<GroupResource>
                {
                    Node("Verkauf", customers: 5, shifts: 3, employees: 10,
                        Node("Verkauf Nord", customers: 2, shifts: 1, employees: 4)),
                    Node("Leer")
                }
            });
        var skill = new GetDashboardOverviewSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("7 customer assignment(s)");
        result.Message.ShouldContain("4 shift(s)");
        result.Message.ShouldContain("14 employee(s)");
        result.Message.ShouldContain("2 group(s) with data");
    }

    [Test]
    public async Task ShortCircuits_WhenRestrictedWithoutVisibleGroups()
    {
        WireVisibility(restricted: true, hasGroups: false);
        var skill = new GetDashboardOverviewSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("no groups are");
        result.Message.ShouldContain("set_user_group_scope");
        await _mediator.DidNotReceive().Send(Arg.Any<GetGroupTreeQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MentionsRestriction_WhenScopeIsLimited()
    {
        WireVisibility(restricted: true, hasGroups: true);
        _mediator.Send(Arg.Any<GetGroupTreeQuery>(), Arg.Any<CancellationToken>())
            .Returns(new GroupTreeResource
            {
                Nodes = new List<GroupResource> { Node("Verkauf", customers: 1) }
            });
        var skill = new GetDashboardOverviewSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("limited to the groups visible");
    }

    [Test]
    public async Task LimitsToLargestGroups_AndSaysSo()
    {
        WireVisibility(restricted: false, hasGroups: true);
        _mediator.Send(Arg.Any<GetGroupTreeQuery>(), Arg.Any<CancellationToken>())
            .Returns(new GroupTreeResource
            {
                Nodes = new List<GroupResource>
                {
                    Node("A", customers: 9),
                    Node("B", customers: 5),
                    Node("C", customers: 1)
                }
            });
        var skill = new GetDashboardOverviewSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["limit"] = 2 });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("2 largest of 3 groups");
    }
}
