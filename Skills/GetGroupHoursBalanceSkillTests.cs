// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GetGroupHoursBalanceSkill — group resolution failure, empty/filtered
/// membership, balance computation and ascending sort by balance.
/// </summary>

using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Groups;
using Klacks.Api.Application.Queries.PeriodHours;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.DTOs.Schedules;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetGroupHoursBalanceSkillTests
{
    private const string StartDateText = "2026-07-01";
    private const string EndDateText = "2026-07-31";

    private IGroupRepository _groupRepository = null!;
    private IMediator _mediator = null!;
    private Group _group = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _mediator = Substitute.For<IMediator>();

        _group = new Group { Id = Guid.NewGuid(), Name = "Filiale Bern" };
        _groupRepository.List().Returns(new List<Group> { _group });
    }

    private GetGroupHoursBalanceSkill Skill() =>
        new(_groupRepository, TestGroupScopeGuard.Unrestricted(), _mediator);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts" }
    };

    private static Dictionary<string, object> Parameters(string groupName) => new()
    {
        ["groupName"] = groupName,
        ["startDate"] = StartDateText,
        ["endDate"] = EndDateText
    };

    private static GroupItemResource EmployeeMember(
        Guid clientId,
        string firstName,
        string lastName,
        bool isDeleted = false,
        EntityTypeEnum type = EntityTypeEnum.Employee,
        DateTime? validFrom = null,
        DateTime? validUntil = null) => new()
        {
            ClientId = clientId,
            ValidFrom = validFrom,
            ValidUntil = validUntil,
            Client = new ClientResource
            {
                Id = clientId,
                FirstName = firstName,
                Name = lastName,
                IsDeleted = isDeleted,
                Type = (int)type
            }
        };

    private void SetMembers(params GroupItemResource[] members) =>
        _mediator.Send(Arg.Any<GetGroupMembersQuery>(), Arg.Any<CancellationToken>())
            .Returns(members.ToList());

    private void SetPeriodHours(Dictionary<Guid, PeriodHoursResource> hoursByClient) =>
        _mediator.Send(Arg.Any<GetPeriodHoursQuery>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var query = ci.Arg<GetPeriodHoursQuery>();
                var filtered = query.Request.ClientIds
                    .Where(hoursByClient.ContainsKey)
                    .ToDictionary(id => id, id => hoursByClient[id]);
                return Task.FromResult(filtered);
            });

    [Test]
    public async Task ReturnsError_WhenGroupNameUnknown()
    {
        var result = await Skill().ExecuteAsync(Ctx(), Parameters("Gibt Es Nicht"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
    }

    [Test]
    public async Task ReturnsError_WhenEndDateBeforeStartDate()
    {
        var parameters = new Dictionary<string, object>
        {
            ["groupName"] = "Filiale Bern",
            ["startDate"] = EndDateText,
            ["endDate"] = StartDateText
        };

        var result = await Skill().ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("must not be before");
        await _mediator.DidNotReceive().Send(Arg.Any<GetGroupMembersQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReportsEmptyResult_WhenGroupHasNoActiveEmployeeMembers()
    {
        SetMembers();

        var result = await Skill().ExecuteAsync(Ctx(), Parameters("Filiale Bern"));

        result.Success.ShouldBeTrue(result.Message);
        var data = result.Data.ShouldBeOfType<GroupHoursBalanceResult>();
        data.MemberCount.ShouldBe(0);
        data.Members.ShouldBeEmpty();
        result.Message.ShouldContain("no active employee members");
    }

    [Test]
    public async Task ExcludesNonEmployeeAndDeletedAndOutOfPeriodMembers()
    {
        var employeeId = Guid.NewGuid();
        SetMembers(
            EmployeeMember(employeeId, "Anna", "Müller"),
            EmployeeMember(Guid.NewGuid(), "Carla", "Customer", type: EntityTypeEnum.Customer),
            EmployeeMember(Guid.NewGuid(), "Deleted", "Person", isDeleted: true),
            EmployeeMember(
                Guid.NewGuid(), "Future", "Joiner",
                validFrom: new DateTime(2026, 8, 1, 0, 0, 0, DateTimeKind.Utc)),
            new() { ShiftId = Guid.NewGuid() });
        SetPeriodHours(new Dictionary<Guid, PeriodHoursResource>
        {
            [employeeId] = new PeriodHoursResource { Hours = 160m, Surcharges = 5m, GuaranteedHours = 160m }
        });

        var result = await Skill().ExecuteAsync(Ctx(), Parameters("Filiale Bern"));

        result.Success.ShouldBeTrue(result.Message);
        var data = result.Data.ShouldBeOfType<GroupHoursBalanceResult>();
        data.MemberCount.ShouldBe(1);
        data.Members.Single().ClientId.ShouldBe(employeeId);
    }

    [Test]
    public async Task ComputesBalance_AsActualMinusTarget()
    {
        var clientId = Guid.NewGuid();
        SetMembers(EmployeeMember(clientId, "Anna", "Müller"));
        SetPeriodHours(new Dictionary<Guid, PeriodHoursResource>
        {
            [clientId] = new PeriodHoursResource { Hours = 190m, Surcharges = 10m, GuaranteedHours = 170m }
        });

        var result = await Skill().ExecuteAsync(Ctx(), Parameters("Filiale Bern"));

        result.Success.ShouldBeTrue(result.Message);
        var data = result.Data.ShouldBeOfType<GroupHoursBalanceResult>();
        var member = data.Members.Single();
        member.ActualHours.ShouldBe(190m);
        member.TargetHours.ShouldBe(170m);
        member.Balance.ShouldBe(20m);
        result.Message.ShouldContain("+20");
    }

    [Test]
    public async Task SortsMembers_AscendingByBalance()
    {
        var overtimeId = Guid.NewGuid();
        var underId = Guid.NewGuid();
        var evenId = Guid.NewGuid();
        SetMembers(
            EmployeeMember(overtimeId, "Over", "Time"),
            EmployeeMember(underId, "Under", "Target"),
            EmployeeMember(evenId, "Even", "Steven"));
        SetPeriodHours(new Dictionary<Guid, PeriodHoursResource>
        {
            [overtimeId] = new PeriodHoursResource { Hours = 200m, GuaranteedHours = 170m },
            [underId] = new PeriodHoursResource { Hours = 120m, GuaranteedHours = 170m },
            [evenId] = new PeriodHoursResource { Hours = 170m, GuaranteedHours = 170m }
        });

        var result = await Skill().ExecuteAsync(Ctx(), Parameters("Filiale Bern"));

        result.Success.ShouldBeTrue(result.Message);
        var data = result.Data.ShouldBeOfType<GroupHoursBalanceResult>();
        data.Members.Select(m => m.ClientId).ShouldBe(new[] { underId, evenId, overtimeId });
        result.Message.ShouldContain("Lowest: Under Target");
        result.Message.ShouldContain("Highest: Over Time");
    }
}
