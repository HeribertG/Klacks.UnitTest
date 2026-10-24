// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the membership-start behaviour every bulk client-grouping skill shares: validFrom='entry' selects the
/// entry-date mode (no date sent, no "missing start date" refusal on apply), and the structured start facts of the result
/// reach the preview and the applied message as the deterministic sentence (start date, blue and locked days, how many
/// persons entered earlier, the question for the entry-date alternative in the preview only).
/// </summary>

using Klacks.Api.Application.Commands.Grouping;
using Klacks.Api.Application.Commands.Groups;
using Klacks.Api.Application.DTOs.Grouping;
using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GroupingSkillsMembershipStartTests
{
    private const string BlueAndLocked = "blue and locked";
    private const string AskMarker = "validFrom='entry'";

    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();

    private static readonly GroupMembershipStartInfo EarlierEntries =
        new(GroupMembershipStartMode.Fixed, true, "2026-10-20", "2026-10-20", "2026-10-20", 5, 3, 0, "2024-02-01");

    private static readonly GroupMembershipStartInfo NoEarlierEntries =
        new(GroupMembershipStartMode.Fixed, true, "2026-10-20", "2026-10-20", "2026-10-20", 5, 0, 0, null);

    private static readonly GroupMembershipStartInfo EntryMode =
        new(GroupMembershipStartMode.EntryDate, true, null, "2019-03-01", "2025-08-15", 5, 0, 1, null);

    private IGroupRepository _groupRepository = null!;
    private IMediator _mediator = null!;
    private ICompanyClock _companyClock = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupRepository.List().Returns(new List<Group> { new() { Id = GroupId, Name = "Bern" } });
        _mediator = Substitute.For<IMediator>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>())
            .Returns(new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc));
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients", "CanViewGroups" },
        SelectedEntityIds = new List<Guid> { ClientId }
    };

    private static List<ClientSearchItem> OneClient() =>
        new() { new() { Id = ClientId, FirstName = "Max", LastName = "Müller" } };

    private void GivenPartitionAddress(GroupMembershipStartInfo info, bool applied) =>
        _mediator.Send(Arg.Any<PartitionClientsByAddressCommand>(), Arg.Any<CancellationToken>())
            .Returns(new PartitionClientsByAddressResult(
                applied, "Cluster", "Employee", 5, 0, 0, applied ? 5 : 0, applied ? 5 : 0, 0,
                [new PartitionGroupSummary("Bern", null, false, null, 5)], [], [], 0, null, info));

    private PartitionClientsByAddressSkill PartitionAddress() =>
        new(_groupRepository, TestGroupScopeGuard.Unrestricted(), _mediator, _companyClock);

    [Test]
    public async Task PartitionAddress_Preview_StatesTheStartAndAsksWhenSomeEnteredEarlier()
    {
        GivenPartitionAddress(EarlierEntries, applied: false);

        var result = await PartitionAddress().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("2026-10-20");
        result.Message.ShouldContain(BlueAndLocked);
        result.Message.ShouldContain("3 of 5 person(s)");
        result.Message.ShouldContain("2024-02-01");
        result.Message.ShouldContain(AskMarker);
    }

    [Test]
    public async Task PartitionAddress_Applied_StatesTheStartWithoutAskingAgain()
    {
        GivenPartitionAddress(EarlierEntries, applied: true);

        var result = await PartitionAddress().ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["apply"] = true, ["validFrom"] = "2026-10-20" });

        result.Message.ShouldContain("start on 2026-10-20");
        result.Message.ShouldContain(BlueAndLocked);
        result.Message.ShouldNotContain(AskMarker);
    }

    [Test]
    public async Task PartitionAddress_NobodyEnteredEarlier_StatesTheDateButDoesNotAsk()
    {
        GivenPartitionAddress(NoEarlierEntries, applied: false);

        var result = await PartitionAddress().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Message.ShouldContain(BlueAndLocked);
        result.Message.ShouldNotContain(AskMarker);
    }

    [Test]
    public async Task PartitionAddress_EntryKeyword_SendsEntryDateModeWithoutADate()
    {
        GivenPartitionAddress(EntryMode, applied: true);

        var result = await PartitionAddress().ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["apply"] = true, ["validFrom"] = "entry" });

        await _mediator.Received(1).Send(
            Arg.Is<PartitionClientsByAddressCommand>(c =>
                c.StartMode == GroupMembershipStartMode.EntryDate && c.ValidFrom == null && c.Apply),
            Arg.Any<CancellationToken>());
        result.Message.ShouldContain("each person's own employment entry date");
        result.Message.ShouldContain("2019-03-01");
    }

    [Test]
    public async Task PartitionAddress_WithoutValidFrom_SendsFixedMode()
    {
        GivenPartitionAddress(NoEarlierEntries, applied: false);

        await PartitionAddress().ExecuteAsync(Ctx(), new Dictionary<string, object>());

        await _mediator.Received(1).Send(
            Arg.Is<PartitionClientsByAddressCommand>(c => c.StartMode == GroupMembershipStartMode.Fixed && c.ValidFrom == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PartitionQualification_EntryKeyword_SendsEntryDateMode_AndReportsTheStart()
    {
        _mediator.Send(Arg.Any<PartitionClientsByQualificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(new PartitionClientsByQualificationResult(
                true, "Employee", "Qualifikationen", true, GroupId, false, 2, 5, 5, 0, 0, 5, 5, 0,
                [new QualificationGroupSummary("BLS", false, GroupId, 5, 5)], [], [], 0, 0, 0, EntryMode));
        var skill = new PartitionClientsByQualificationSkill(
            _groupRepository, TestGroupScopeGuard.Unrestricted(), _mediator, _companyClock);

        var result = await skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["apply"] = true, ["validFrom"] = "entry" });

        await _mediator.Received(1).Send(
            Arg.Is<PartitionClientsByQualificationCommand>(c => c.StartMode == GroupMembershipStartMode.EntryDate),
            Arg.Any<CancellationToken>());
        result.Message.ShouldContain("each person's own employment entry date");
    }

    [Test]
    public async Task PartitionQualification_Preview_StatesTheStartAndAsks()
    {
        _mediator.Send(Arg.Any<PartitionClientsByQualificationCommand>(), Arg.Any<CancellationToken>())
            .Returns(new PartitionClientsByQualificationResult(
                false, "Employee", "Qualifikationen", true, GroupId, false, 2, 5, 5, 0, 0, 5, 0, 0,
                [new QualificationGroupSummary("BLS", false, GroupId, 5, 5)], [], [], 0, 0, 0, EarlierEntries));
        var skill = new PartitionClientsByQualificationSkill(
            _groupRepository, TestGroupScopeGuard.Unrestricted(), _mediator, _companyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Message.ShouldContain(BlueAndLocked);
        result.Message.ShouldContain(AskMarker);
    }

    [Test]
    public async Task GroupUngroupedByCity_ApplyWithEntryKeyword_IsNotRefusedForAMissingDate()
    {
        _mediator.Send(Arg.Any<GroupUngroupedByCityNameCommand>(), Arg.Any<CancellationToken>())
            .Returns(new GroupUngroupedByCityNameResult(
                true, 5, 5, 5, 5, 0, [new GroupCityAssignment(ClientId, "Max Müller", "Bern", GroupId, "Bern")], [], EntryMode));
        var skill = new GroupUngroupedByCityNameSkill(_mediator, TestGroupScopeGuard.Unrestricted(), _companyClock);

        var result = await skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["apply"] = true, ["validFrom"] = "entry" });

        result.Success.ShouldBeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<GroupUngroupedByCityNameCommand>(c => c.StartMode == GroupMembershipStartMode.EntryDate && c.ValidFrom == null),
            Arg.Any<CancellationToken>());
        result.Message.ShouldContain("each person's own employment entry date");
    }

    [Test]
    public async Task GroupUngroupedByCity_ApplyWithoutAnyStart_StillAsksForOne()
    {
        var skill = new GroupUngroupedByCityNameSkill(_mediator, TestGroupScopeGuard.Unrestricted(), _companyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["apply"] = true });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("entry");
    }

    [Test]
    public async Task GroupUngroupedByCity_Preview_StatesTheStartAndAsks()
    {
        _mediator.Send(Arg.Any<GroupUngroupedByCityNameCommand>(), Arg.Any<CancellationToken>())
            .Returns(new GroupUngroupedByCityNameResult(
                false, 5, 5, 0, 0, 0, [new GroupCityAssignment(ClientId, "Max Müller", "Bern", GroupId, "Bern")], [], EarlierEntries));
        var skill = new GroupUngroupedByCityNameSkill(_mediator, TestGroupScopeGuard.Unrestricted(), _companyClock);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["validFrom"] = "2026-10-20" });

        result.Message.ShouldContain(BlueAndLocked);
        result.Message.ShouldContain(AskMarker);
    }

    [Test]
    public async Task FillGroupByCriteria_ApplyWithEntryKeyword_SendsEntryDateMode()
    {
        _mediator.Send(Arg.Any<FillGroupByCriteriaCommand>(), Arg.Any<CancellationToken>())
            .Returns(new FillGroupByCriteriaResult(true, "Bern", 1, 1, 1, 0, OneClient(), EntryMode));
        var skill = new FillGroupByCriteriaSkill(
            _groupRepository, TestGroupScopeGuard.Unrestricted(), Substitute.For<IContractRepository>(), _mediator, _companyClock,
            TestAddressLocationResolver.SwissAndGerman());

        var result = await skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["groupName"] = "Bern", ["apply"] = true, ["validFrom"] = "entry" });

        result.Success.ShouldBeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<FillGroupByCriteriaCommand>(c => c.StartMode == GroupMembershipStartMode.EntryDate && c.ValidFrom == null),
            Arg.Any<CancellationToken>());
        result.Message.ShouldContain("each person's own employment entry date");
    }

    [Test]
    public async Task FillGroupByCriteria_Preview_StatesTheStartAndAsks()
    {
        _mediator.Send(Arg.Any<FillGroupByCriteriaCommand>(), Arg.Any<CancellationToken>())
            .Returns(new FillGroupByCriteriaResult(false, "Bern", 1, 0, 0, 0, OneClient(), EarlierEntries));
        var skill = new FillGroupByCriteriaSkill(
            _groupRepository, TestGroupScopeGuard.Unrestricted(), Substitute.For<IContractRepository>(), _mediator, _companyClock,
            TestAddressLocationResolver.SwissAndGerman());

        var result = await skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["groupName"] = "Bern", ["validFrom"] = "2026-10-20" });

        result.Message.ShouldContain(BlueAndLocked);
        result.Message.ShouldContain(AskMarker);
    }

    [Test]
    public async Task AddSelectedClients_ApplyWithEntryKeyword_SendsEntryDateMode()
    {
        _mediator.Send(Arg.Any<AddSelectedClientsToGroupCommand>(), Arg.Any<CancellationToken>())
            .Returns(new AddSelectedClientsToGroupResult(true, "Bern", 1, 1, 0, 1, 1, 1, 0, OneClient(), EntryMode));
        var skill = new AddSelectedClientsToGroupSkill(
            _groupRepository, TestGroupScopeGuard.Unrestricted(), _mediator, _companyClock);

        var result = await skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["groupName"] = "Bern", ["apply"] = true, ["validFrom"] = "entry" });

        result.Success.ShouldBeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<AddSelectedClientsToGroupCommand>(c => c.StartMode == GroupMembershipStartMode.EntryDate && c.ValidFrom == null),
            Arg.Any<CancellationToken>());
        result.Message.ShouldContain("each person's own employment entry date");
    }

    [Test]
    public async Task AddSelectedClients_Preview_StatesTheStartAndAsks()
    {
        _mediator.Send(Arg.Any<AddSelectedClientsToGroupCommand>(), Arg.Any<CancellationToken>())
            .Returns(new AddSelectedClientsToGroupResult(false, "Bern", 1, 1, 0, 1, 0, 0, 0, OneClient(), EarlierEntries));
        var skill = new AddSelectedClientsToGroupSkill(
            _groupRepository, TestGroupScopeGuard.Unrestricted(), _mediator, _companyClock);

        var result = await skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["groupName"] = "Bern", ["validFrom"] = "2026-10-20" });

        result.Message.ShouldContain(BlueAndLocked);
        result.Message.ShouldContain(AskMarker);
    }

    [Test]
    public async Task ApplyGrouping_EntryKeyword_SendsEntryDateMode_AndReportsTheStart()
    {
        _mediator.Send(Arg.Any<ApplyCustomerGroupingCommand>(), Arg.Any<CancellationToken>())
            .Returns(new CustomerGroupingApplyResult(5, 5, 0, 0, new DateTime(2019, 3, 1, 0, 0, 0, DateTimeKind.Utc), EntryMode));

        var result = await new ApplyGroupingSkill(_mediator, _companyClock).ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["entityType"] = "Employee", ["validFrom"] = "entry" });

        result.Success.ShouldBeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<ApplyCustomerGroupingCommand>(c => c.StartMode == GroupMembershipStartMode.EntryDate && c.ValidFrom == null),
            Arg.Any<CancellationToken>());
        result.Message.ShouldContain("each person's own employment entry date");
    }

    [Test]
    public async Task ApplyGrouping_WithEarlierEntries_StatesTheBlueLockedConsequence()
    {
        _mediator.Send(Arg.Any<ApplyCustomerGroupingCommand>(), Arg.Any<CancellationToken>())
            .Returns(new CustomerGroupingApplyResult(5, 5, 0, 0, new DateTime(2026, 10, 20, 0, 0, 0, DateTimeKind.Utc), EarlierEntries));

        var result = await new ApplyGroupingSkill(_mediator, _companyClock).ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["entityType"] = "Employee" });

        result.Message.ShouldContain(BlueAndLocked);
        result.Message.ShouldContain("3 of 5 person(s)");
    }
}
