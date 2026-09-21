// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The start rules of the approval chain, each a fail-closed property: one Running chain per condition,
/// no restart on the company day an earlier chain ended on, no chain without an eligible approver, no
/// chain for a remediation skill the registry does not know, and - when everything is in place - a chain
/// whose roster is the rights-filtered one and whose deadline is one approval window per stage.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Application.Services.Assistant.Escalation;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant.Escalation;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Services.Assistant.Conditions;

[TestFixture]
public class ConditionApprovalChainStarterTests
{
    private const string SkillName = "create_container_template";
    private const string RequiredPermission = Permissions.CanEditClients;

    private static readonly DateTime NowUtc = new(2026, 9, 21, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime CompanyDayStartUtc = new(2026, 9, 20, 22, 0, 0, DateTimeKind.Utc);

    private static readonly IReadOnlyList<EscalationRosterCandidate> Roster =
    [
        new(Guid.NewGuid().ToString(), "Planner"),
        new(Guid.NewGuid().ToString(), "Admin")
    ];

    private FakeEscalationChainRepository _chainRepository = null!;
    private IEscalationChainService _chainService = null!;
    private IConditionApprovalRosterResolver _rosterResolver = null!;
    private ISkillRegistry _skillRegistry = null!;
    private ISettingsReader _settingsReader = null!;
    private ConditionApprovalChainStarter _sut = null!;
    private AgentCondition _condition = null!;
    private ConditionRemediationEntry _entry = null!;

    [SetUp]
    public void SetUp()
    {
        _chainRepository = new FakeEscalationChainRepository();
        _chainService = Substitute.For<IEscalationChainService>();
        _rosterResolver = Substitute.For<IConditionApprovalRosterResolver>();
        _skillRegistry = Substitute.For<ISkillRegistry>();
        _settingsReader = Substitute.For<ISettingsReader>();

        _settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);
        _skillRegistry.GetSkillByName(SkillName).Returns(new SkillDescriptor(
            SkillName, "test", SkillCategory.Action, Array.Empty<SkillParameter>(),
            [RequiredPermission], Array.Empty<LLMCapability>(), null));
        _rosterResolver
            .ResolveAsync(Arg.Any<AgentCondition>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Roster);
        _chainService
            .StartConditionApprovalChainAsync(Arg.Any<StartConditionApprovalChainRequest>(), Arg.Any<CancellationToken>())
            .Returns(Guid.NewGuid());

        _condition = new AgentCondition
        {
            Id = Guid.NewGuid(),
            TriggerKind = AgentTriggerKinds.EmptyContainer,
            GroupId = Guid.NewGuid(),
            Status = AgentConditionStatus.Reported
        };
        _entry = new ConditionRemediationEntry(SkillName, Substitute.For<IConditionRemediationParameterBinder>(), []);

        _sut = new ConditionApprovalChainStarter(
            _chainRepository,
            _chainService,
            _rosterResolver,
            _skillRegistry,
            _settingsReader,
            new SettableTimeProvider(NowUtc),
            NullLogger<ConditionApprovalChainStarter>.Instance);
    }

    [Test]
    public async Task Start_ResolvesTheRosterWithTheSkillsPermissions_AndDerivesOneWindowPerStage()
    {
        var outcome = await _sut.TryStartAsync(_condition, _entry, CompanyDayStartUtc);

        outcome.ShouldBe(ConditionApprovalStartOutcome.Started);
        await _rosterResolver.Received(1).ResolveAsync(
            _condition, Arg.Is<IReadOnlyCollection<string>>(p => p.Single() == RequiredPermission), Arg.Any<CancellationToken>());
        await _chainService.Received(1).StartConditionApprovalChainAsync(
            Arg.Is<StartConditionApprovalChainRequest>(r =>
                r.ConditionId == _condition.Id
                && r.GroupId == _condition.GroupId
                && ReferenceEquals(r.Roster, Roster)
                && r.DeadlineUtc == NowUtc.AddMinutes(Roster.Count * ProactiveApprovalWindowReader.DefaultWindowMinutes)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Start_WhileAChainIsRunning_DoesNotAskAgain()
    {
        await SeedChainAsync(EscalationChainStatus.Running, NowUtc.AddMinutes(-10));

        var outcome = await _sut.TryStartAsync(_condition, _entry, CompanyDayStartUtc);

        outcome.ShouldBe(ConditionApprovalStartOutcome.ChainAlreadyRunning);
        await _chainService.DidNotReceiveWithAnyArgs().StartConditionApprovalChainAsync(default, default);
        await _rosterResolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default!, default);
    }

    [Test]
    public async Task Start_AfterAChainExhaustedOnTheSameCompanyDay_WaitsForTheNextDay()
    {
        await SeedChainAsync(EscalationChainStatus.Exhausted, CompanyDayStartUtc.AddHours(1));

        var outcome = await _sut.TryStartAsync(_condition, _entry, CompanyDayStartUtc);

        outcome.ShouldBe(ConditionApprovalStartOutcome.WaitingForNextCompanyDay);
        await _chainService.DidNotReceiveWithAnyArgs().StartConditionApprovalChainAsync(default, default);
    }

    [Test]
    public async Task Start_AfterAChainExhaustedOnAnEarlierCompanyDay_AsksAgain()
    {
        await SeedChainAsync(EscalationChainStatus.Exhausted, CompanyDayStartUtc.AddMinutes(-1));

        var outcome = await _sut.TryStartAsync(_condition, _entry, CompanyDayStartUtc);

        outcome.ShouldBe(ConditionApprovalStartOutcome.Started);
    }

    [Test]
    public async Task Start_WithNobodyEligible_FailsClosedWithoutAChain()
    {
        _rosterResolver
            .ResolveAsync(Arg.Any<AgentCondition>(), Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<EscalationRosterCandidate>());

        var outcome = await _sut.TryStartAsync(_condition, _entry, CompanyDayStartUtc);

        outcome.ShouldBe(ConditionApprovalStartOutcome.NoEligibleApprover);
        await _chainService.DidNotReceiveWithAnyArgs().StartConditionApprovalChainAsync(default, default);
    }

    [Test]
    public async Task Start_WithAnUnknownRemediationSkill_FailsClosedWithoutAChain()
    {
        _skillRegistry.GetSkillByName(SkillName).Returns((SkillDescriptor?)null);

        var outcome = await _sut.TryStartAsync(_condition, _entry, CompanyDayStartUtc);

        outcome.ShouldBe(ConditionApprovalStartOutcome.NoEligibleApprover);
        await _rosterResolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default!, default);
    }

    [Test]
    public async Task Start_WhenTheChainServiceDeclines_ReportsNotStarted()
    {
        _chainService
            .StartConditionApprovalChainAsync(Arg.Any<StartConditionApprovalChainRequest>(), Arg.Any<CancellationToken>())
            .Returns((Guid?)null);

        var outcome = await _sut.TryStartAsync(_condition, _entry, CompanyDayStartUtc);

        outcome.ShouldBe(ConditionApprovalStartOutcome.NotStarted);
    }

    private async Task SeedChainAsync(EscalationChainStatus status, DateTime createdUtc)
    {
        await _chainRepository.AddAsync(new EscalationChain
        {
            Id = Guid.NewGuid(),
            Purpose = EscalationChainPurpose.ProactiveApproval,
            ConditionId = _condition.Id,
            Status = status,
            CreateTime = createdUtc,
            DeadlineUtc = createdUtc.AddHours(1)
        });
    }
}
