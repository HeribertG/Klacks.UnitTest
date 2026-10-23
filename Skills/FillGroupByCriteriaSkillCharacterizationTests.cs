// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for FillGroupByCriteriaSkill.ExecuteAsync, written before its long body was split into
/// contract, qualification and command steps: the exact refusal texts for unknown and ambiguous contracts and
/// qualifications (with and without existing entries), that a contract refusal stops before qualifications are read,
/// the entity-type mapping and trimming forwarded to the command, that the canton is resolved before a missing start
/// date is refused, the preview, empty and apply texts, and that a verification exception becomes an error.
/// </summary>

using Klacks.Api.Application.Commands.Groups;
using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Queries.Qualifications;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class FillGroupByCriteriaSkillCharacterizationTests
{
    private const string RandomSelectionNote =
        " The clients were drawn at random; calling again today with the same parameters, random=true included, " +
        "and apply=true adds exactly these.";

    private static readonly Guid BernGroupId = Guid.NewGuid();
    private static readonly Guid ContractId = Guid.NewGuid();

    private IGroupRepository _groupRepository = null!;
    private IContractRepository _contractRepository = null!;
    private IMediator _mediator = null!;
    private FillGroupByCriteriaSkill _skill = null!;
    private FillGroupByCriteriaResult _commandResult = null!;
    private Qualification _firstAid = null!;

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _contractRepository = Substitute.For<IContractRepository>();
        _mediator = Substitute.For<IMediator>();
        var companyClock = Substitute.For<ICompanyClock>();
        companyClock.GetTodayAsync(Arg.Any<CancellationToken>())
            .Returns(new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc));
        _skill = new FillGroupByCriteriaSkill(
            _groupRepository, TestGroupScopeGuard.Unrestricted(), _contractRepository, _mediator, companyClock);

        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = BernGroupId, Name = "Bern" },
            new() { Id = Guid.NewGuid(), Name = "Zürich" }
        });

        _contractRepository.List().Returns(new List<Contract>
        {
            new() { Id = ContractId, Name = "180 BE" },
            new() { Id = Guid.NewGuid(), Name = "Old 180", IsDeleted = true }
        });

        _firstAid = new Qualification
        {
            Id = Guid.NewGuid(),
            Name = new MultiLanguage { De = "Erste Hilfe", En = "First Aid", Fr = "Premiers secours", It = "Primo soccorso" }
        };
        _mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<Qualification> { _firstAid }.AsEnumerable());

        _mediator.Send(Arg.Any<ListQuery<StateResource>>(), Arg.Any<CancellationToken>())
            .Returns(new List<StateResource>
            {
                new() { Abbreviation = "BE", Name = new MultiLanguage { De = "Bern", En = "Bern", Fr = "Berne", It = "Berna" } }
            }.AsEnumerable());

        _commandResult = new FillGroupByCriteriaResult(
            false, "Bern", 1, 0, 0, 0,
            new List<ClientSearchItem> { new() { Id = Guid.NewGuid(), FirstName = "Max", LastName = "Müller" } });
        _mediator.Send(Arg.Any<FillGroupByCriteriaCommand>(), Arg.Any<CancellationToken>())
            .Returns(_ => _commandResult);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients", "CanViewGroups" }
    };

    private Task<SkillResult> RunAsync(params (string Key, object Value)[] extra)
    {
        var parameters = new Dictionary<string, object> { ["groupName"] = "Bern" };
        foreach (var (key, value) in extra)
        {
            parameters[key] = value;
        }

        return _skill.ExecuteAsync(Ctx(), parameters);
    }

    [Test]
    public async Task InvalidValidFrom_IsRejected_BeforeGroupsAreListed()
    {
        var result = await RunAsync(("validFrom", "irgendwann"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(SkillDateParser.InvalidDateMessageFor("validFrom", "irgendwann"));
        await _groupRepository.DidNotReceive().List();
    }

    [Test]
    public async Task UnknownContract_ListsTheUndeletedContracts_AndStopsBeforeQualifications()
    {
        var result = await RunAsync(("contractName", "Night"), ("qualificationName", "Nope"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "No contract found matching 'Night'. Available contracts: 180 BE. " +
            "Offer the user only these real contract names — do not invent contracts.");
        await _mediator.DidNotReceive().Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownContract_WithoutAnyContract_SaysThereAreNone()
    {
        _contractRepository.List().Returns(new List<Contract>
        {
            new() { Id = Guid.NewGuid(), Name = "Old 180", IsDeleted = true }
        });

        var result = await RunAsync(("contractName", "180"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "No contract found matching '180'. There are no contracts yet. " +
            "Offer the user only these real contract names — do not invent contracts.");
    }

    [Test]
    public async Task AmbiguousContract_ReturnsExactText()
    {
        _contractRepository.List().Returns(new List<Contract>
        {
            new() { Id = Guid.NewGuid(), Name = "180 BE" },
            new() { Id = Guid.NewGuid(), Name = "180 BE night" }
        });

        var result = await RunAsync(("contractName", "180"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Multiple contracts match '180': '180 BE', '180 BE night'. Please be more specific.");
    }

    [Test]
    public async Task UnknownQualification_ListsTheQualifications()
    {
        var result = await RunAsync(("contractName", "180"), ("qualificationName", "Forklift"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "No qualification found matching 'Forklift'. Available qualifications: " +
            QualificationResolver.DisplayName(_firstAid) + ". " +
            "Offer the user only these real qualification names — do not invent qualifications.");
        await _contractRepository.Received(1).List();
    }

    [Test]
    public async Task UnknownQualification_WithoutAnyQualification_SaysThereAreNone()
    {
        _mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<Qualification>().AsEnumerable());

        var result = await RunAsync(("qualificationName", "Forklift"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            "No qualification found matching 'Forklift'. There are no qualifications yet. " +
            "Offer the user only these real qualification names — do not invent qualifications.");
    }

    [Test]
    public async Task AmbiguousQualification_ReturnsExactText()
    {
        var plus = new Qualification { Id = Guid.NewGuid(), Name = new MultiLanguage { De = "Erste Hilfe Plus" } };
        _mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<Qualification> { _firstAid, plus }.AsEnumerable());

        var result = await RunAsync(("qualificationName", "Erste Hilfe"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            $"Multiple qualifications match 'Erste Hilfe': '{QualificationResolver.DisplayName(_firstAid)}', " +
            $"'{QualificationResolver.DisplayName(plus)}'. Please be more specific.");
    }

    [Test]
    public async Task ApplyWithoutStartDate_IsRefused_AfterTheCantonWasResolved()
    {
        var result = await RunAsync(("apply", true), ("canton", "Bern"), ("entityType", "Customer"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(SkillDateParser.MissingStartDateMessage("add the matching Customer(s) to group 'Bern'"));
        await _mediator.Received(1).Send(Arg.Any<ListQuery<StateResource>>(), Arg.Any<CancellationToken>());
        await _mediator.DidNotReceive().Send(Arg.Any<FillGroupByCriteriaCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Command_CarriesResolvedAndTrimmedCriteria()
    {
        var result = await RunAsync(
            ("canton", "Berne"), ("city", " Thun "), ("zipPrefix", " 36 "), ("contractName", "180"),
            ("qualificationName", "First Aid"), ("entityType", "ExternEmp"), ("count", 3), ("validFrom", "2026-07-01"));

        result.Success.ShouldBeTrue(result.Message);
        await _mediator.Received(1).Send(
            Arg.Is<FillGroupByCriteriaCommand>(c =>
                c.GroupId == BernGroupId &&
                c.GroupName == "Bern" &&
                c.Canton == "BE" &&
                c.ContractId == ContractId &&
                c.City == "Thun" &&
                c.ZipPrefix == "36" &&
                c.QualificationId == _firstAid.Id &&
                c.EntityType == EntityTypeEnum.ExternEmp &&
                c.Count == 3 &&
                c.ValidFrom == new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc) &&
                !c.Apply &&
                c.UserName == "tester" &&
                c.StartMode == GroupMembershipStartMode.Fixed &&
                c.RandomSeed == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownEntityType_FallsBackToEmployee()
    {
        await RunAsync(("entityType", "Robot"), ("validFrom", "2026-07-01"));

        await _mediator.Received(1).Send(
            Arg.Is<FillGroupByCriteriaCommand>(c => c.EntityType == EntityTypeEnum.Employee && c.Canton == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Preview_WithStartDate_ExactText()
    {
        var result = await RunAsync(("validFrom", "2026-07-01"));

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe(
            "Preview: 1 of 1 matching Employee(s) would be added to group 'Bern': Max Müller. " +
            GroupMembershipStartHint.For(null, applied: false) + " " +
            "Nothing was changed yet. " +
            "Ask the user to confirm, then call again with apply=true and the same parameters.");
        result.Data.ShouldBeSameAs(_commandResult);
    }

    [Test]
    public async Task RandomPreview_WithoutStartDate_AsksForItAndNotesTheDraw()
    {
        var result = await RunAsync(("random", true), ("count", 1));

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe(
            "Preview: 1 of 1 matching Employee(s) would be added to group 'Bern': Max Müller. " +
            GroupMembershipStartHint.For(null, applied: false) + " " +
            "Nothing was changed yet." + SkillDateParser.AskForStartDateInPreview + RandomSelectionNote + " " +
            "Ask the user to confirm, then call again with apply=true and the same parameters.");
    }

    [Test]
    public async Task NoMatches_ExactText()
    {
        _commandResult = new FillGroupByCriteriaResult(false, "Bern", 0, 0, 0, 0, new List<ClientSearchItem>());

        var result = await RunAsync(("entityType", "Customer"), ("validFrom", "2026-07-01"));

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("No Customer found matching the criteria for group 'Bern'.");
    }

    [Test]
    public async Task Apply_ExactText_WithAlreadyMemberNote()
    {
        _commandResult = new FillGroupByCriteriaResult(
            true, "Bern", 3, 2, 2, 1,
            new List<ClientSearchItem> { new() { Id = Guid.NewGuid(), FirstName = "Max", LastName = "Müller" } });

        var result = await RunAsync(("apply", true), ("validFrom", "2026-07-01"));

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe(
            "Added 2 Employee(s) to group 'Bern' and confirmed 2 in the database (verified) (1 were already members). " +
            GroupMembershipStartHint.For(null, applied: true));
    }

    [Test]
    public async Task VerificationException_BecomesAnError()
    {
        _mediator.Send(Arg.Any<FillGroupByCriteriaCommand>(), Arg.Any<CancellationToken>())
            .Returns<FillGroupByCriteriaResult>(_ => throw new SkillVerificationException("fill_group_by_criteria", "not verified"));

        var result = await RunAsync(("apply", true), ("validFrom", "2026-07-01"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("not verified");
    }
}
