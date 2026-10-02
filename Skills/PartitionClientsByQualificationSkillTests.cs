// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Commands.Groups;
using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class PartitionClientsByQualificationSkillTests
{
    private const string PreviewMarker = "Nothing was changed yet";
    private const string ConfirmInstruction = "Ask the user to confirm, then call again with apply=true.";

    private static readonly Guid WinterthurId = Guid.NewGuid();

    private IGroupRepository _groupRepository = null!;
    private IMediator _mediator = null!;
    private ICompanyClock _companyClock = null!;
    private PartitionClientsByQualificationCommand? _sent;

    [SetUp]
    public void SetUp()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = WinterthurId, Name = "Winterthur" },
            new() { Id = Guid.NewGuid(), Name = "Winterthur Nord" },
            new() { Id = Guid.NewGuid(), Name = "Zürich" }
        });
        _mediator = Substitute.For<IMediator>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(new DateTime(2026, 10, 2, 0, 0, 0, DateTimeKind.Utc));
        _mediator.Send(Arg.Any<PartitionClientsByQualificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _sent = ci.Arg<PartitionClientsByQualificationCommand>();
                return Task.FromResult(Result(_sent.Apply));
            });
    }

    [Test]
    public async Task Defaults_EmployeeOnly_IncludeAlreadyGrouped_MinTwo_Preview()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        _sent!.EntityTypes.ShouldBe([EntityTypeEnum.Employee]);
        _sent.IncludeAlreadyGrouped.ShouldBeTrue();
        _sent.MinMembers.ShouldBe(2);
        _sent.Apply.ShouldBeFalse();
        _sent.ScopeGroupId.ShouldBeNull();
    }

    [Test]
    public async Task Preview_ListsGroupsAndSkipped_AndEndsWithConfirmInstruction()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Message.ShouldContain("Staplerfahrer (new, 3 members)");
        result.Message.ShouldContain("Erste Hilfe (already exists, 1 new of 4 members)");
        result.Message.ShouldContain("Kranführer (1)");
        result.Message.ShouldContain(PreviewMarker);
        result.Message.ShouldEndWith(ConfirmInstruction);
    }

    [Test]
    public async Task EntityTypeAll_SendsAllThreeTypes()
    {
        await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["entityType"] = "All" });

        _sent!.EntityTypes.Count.ShouldBe(3);
    }

    [Test]
    public async Task InvalidEntityType_IsRejectedBeforeSending()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["entityType"] = "Robot" });

        result.Success.ShouldBeFalse();
        await _mediator.DidNotReceive().Send(Arg.Any<PartitionClientsByQualificationCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MinMembersBelowOne_IsClampedToOne()
    {
        await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["minMembers"] = -3 });

        _sent!.MinMembers.ShouldBe(1);
    }

    [Test]
    public async Task RootGroupName_ResolvesToExactlyOneGroup_AndIsForwardedAsScope()
    {
        await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["rootGroupName"] = "Winterthur" });

        _sent!.ScopeGroupId.ShouldBe(WinterthurId);
        _sent.ScopeGroupName.ShouldBe("Winterthur");
    }

    [Test]
    public async Task UnknownRootGroupName_ReturnsErrorListingRealGroups()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["rootGroupName"] = "Atlantis" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Zürich");
        await _mediator.DidNotReceive().Send(Arg.Any<PartitionClientsByQualificationCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RestrictedScope_IsRefused()
    {
        var result = await Skill(TestGroupScopeGuard.Restricted([Guid.NewGuid()], "Bern"))
            .ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Bern");
        await _mediator.DidNotReceive().Send(Arg.Any<PartitionClientsByQualificationCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Preview_WarnsAboutVisibilityWidening_WhenRestrictedUsersSeeTheReusedRoot()
    {
        _mediator.Send(Arg.Any<PartitionClientsByQualificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromResult(Result(false) with { RestrictedUsersSeeingRootCount = 2, ClientsNewlyVisibleToThemCount = 7 }));

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Message.ShouldContain("2 restricted user(s) who see 'Qualifikationen' will additionally see 7 people");
    }

    [Test]
    public async Task Apply_RelaysVerifiedCount()
    {
        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["apply"] = true });

        _sent!.Apply.ShouldBeTrue();
        result.Message.ShouldContain("confirmed 5 in the database (verified)");
    }

    [Test]
    public async Task VerificationFailure_IsReturnedAsError()
    {
        _mediator.Send(Arg.Any<PartitionClientsByQualificationCommand>(), Arg.Any<CancellationToken>())
            .Returns<PartitionClientsByQualificationResult>(_ => throw new SkillVerificationException("partition_clients_by_qualification", "rolled back"));

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object> { ["apply"] = true });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("rolled back");
    }

    [Test]
    public async Task AmbiguousRoot_IsReturnedAsError()
    {
        _mediator.Send(Arg.Any<PartitionClientsByQualificationCommand>(), Arg.Any<CancellationToken>())
            .Returns<PartitionClientsByQualificationResult>(_ => throw new InvalidRequestException("two roots"));

        var result = await Skill().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("two roots");
    }

    private PartitionClientsByQualificationSkill Skill(IGroupScopeGuard? scopeGuard = null) =>
        new(_groupRepository, scopeGuard ?? TestGroupScopeGuard.Unrestricted(), _mediator, _companyClock);

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients", "CanCreateGroups", "CanViewGroups" }
    };

    private static PartitionClientsByQualificationResult Result(bool applied) => new(
        Applied: applied,
        EntityType: "Employee",
        ParentGroupName: "Qualifikationen",
        ParentExisted: false,
        ParentGroupId: null,
        IsScoped: false,
        MinMembers: 2,
        TotalClients: 10,
        ConsideredClients: 10,
        SkippedAlreadyGroupedCount: 0,
        ClientsWithoutQualificationCount: 2,
        AssignedCount: applied ? 5 : 4,
        VerifiedCount: applied ? 5 : 0,
        AlreadyMemberCount: 3,
        Groups:
        [
            new QualificationGroupSummary("Erste Hilfe", true, Guid.NewGuid(), 4, 1),
            new QualificationGroupSummary("Staplerfahrer", false, null, 3, 3)
        ],
        SkippedQualifications: [new SkippedQualificationGroup("Kranführer", 1)],
        Warnings: [],
        UsersKeepingFullVisibilityCount: 0);
}
