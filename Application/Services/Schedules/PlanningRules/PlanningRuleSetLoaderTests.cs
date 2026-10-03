// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules.PlanningRules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Models.Scheduling;
using Klacks.Api.Domain.Services.Schedules;
using Klacks.ScheduleOptimizer.Constraints.Rules;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Services.Schedules.PlanningRules;

[TestFixture]
public class PlanningRuleSetLoaderTests
{
    private const string MaxRunJson = """{"schemaVersion":1,"kind":"Night","maxRun":3}""";
    private const string FairnessJson = """{"schemaVersion":1,"metric":"NightDays","window":"PlanPeriod","maxSpread":2}""";

    private static readonly DateOnly From = new(2026, 11, 1);
    private static readonly DateOnly Until = new(2026, 11, 30);

    private readonly Guid _agentA = Guid.NewGuid();
    private readonly Guid _agentB = Guid.NewGuid();
    private readonly Guid _industryRule = Guid.NewGuid();

    private ICounterRuleRepository _counterRules = null!;
    private IPlanningConstraintRepository _constraints = null!;
    private IComplianceEnforcementResolver _enforcement = null!;
    private IClientContractDataProvider _contracts = null!;
    private IGetAllClientIdsFromGroupAndSubgroups _groups = null!;
    private IPlanningRuleDataReader _dataReader = null!;
    private IPlanningRuleCarryInLoader _carryIn = null!;
    private ISettingsReader _settings = null!;
    private IPlanningConstraintPresence _presence = null!;
    private CountingLogger<PlanningRuleSetLoader> _logger = null!;
    private PlanningRuleSetLoader _loader = null!;

    [SetUp]
    public void SetUp()
    {
        _counterRules = Substitute.For<ICounterRuleRepository>();
        _constraints = Substitute.For<IPlanningConstraintRepository>();
        _enforcement = Substitute.For<IComplianceEnforcementResolver>();
        _contracts = Substitute.For<IClientContractDataProvider>();
        _groups = Substitute.For<IGetAllClientIdsFromGroupAndSubgroups>();
        _dataReader = Substitute.For<IPlanningRuleDataReader>();
        _carryIn = Substitute.For<IPlanningRuleCarryInLoader>();

        _counterRules.GetAllApprovedAsync(Arg.Any<CancellationToken>()).Returns([]);
        _constraints.GetApprovedForPeriodAsync(From, Until, Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns([]);
        _enforcement.GetModeAsync(ComplianceRuleNames.CounterRule).Returns(RuleEnforcementMode.Warn);
        _contracts.GetEffectiveContractDataForClientsAsync(Arg.Any<List<Guid>>(), From).Returns(new Dictionary<Guid, EffectiveContractData>
        {
            [_agentA] = new() { SchedulingRuleId = _industryRule, NightStart = "22:00", NightEnd = "05:00", WorkloadPercent = 50m },
            [_agentB] = new() { SchedulingRuleId = Guid.NewGuid() },
        });
        _carryIn.LoadAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<IReadOnlyList<PlanRule>>(),
                Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns([]);

        _settings = Substitute.For<ISettingsReader>();
        _settings.GetSetting(Arg.Any<string>()).Returns((Klacks.Api.Domain.Models.Settings.Settings?)null);

        _presence = Substitute.For<IPlanningConstraintPresence>();
        _presence.AnyApprovedAsync(Arg.Any<CancellationToken>()).Returns(true);
        _logger = new CountingLogger<PlanningRuleSetLoader>();

        _loader = new PlanningRuleSetLoader(
            _counterRules, _constraints, new PlanningConstraintValidator(), _enforcement, _contracts, _groups, _dataReader, _carryIn, _settings, _presence,
            new MemoryCache(new MemoryCacheOptions()), _logger);
    }

    [Test]
    public async Task NoRules_ReturnsEmpty_WithoutResolvingContracts()
    {
        var rules = await _loader.LoadAsync([_agentA], From, Until, null);

        rules.ShouldBeEmpty();
        await _contracts.DidNotReceiveWithAnyArgs().GetEffectiveContractDataForClientsAsync(default!, default);
    }

    [Test]
    public async Task RuleSet_WithoutRules_ReadsNeitherContractsNorCarryIn()
    {
        var set = await _loader.LoadRuleSetAsync([_agentA, _agentB], From, Until, null, coveredBoundaryDays: 0, PlanningRuleSources.PlanningConstraints);

        set.Rules.ShouldBeEmpty();
        set.Agents.Count.ShouldBe(2);
        await _contracts.DidNotReceiveWithAnyArgs().GetEffectiveContractDataForClientsAsync(default!, default);
        await _carryIn.DidNotReceiveWithAnyArgs().LoadAsync(default!, default, default, default!, default, default, default);
    }

    [Test]
    public async Task RuleSet_PlanningConstraintsOnly_DoesNotLoadCounterRules()
    {
        _counterRules.GetAllApprovedAsync(Arg.Any<CancellationToken>()).Returns([Counter(null, null)]);

        var set = await _loader.LoadRuleSetAsync([_agentA], From, Until, null, coveredBoundaryDays: 0, PlanningRuleSources.PlanningConstraints);

        set.Rules.ShouldBeEmpty();
        await _counterRules.DidNotReceiveWithAnyArgs().GetAllApprovedAsync(default);
    }

    [Test]
    public async Task NoApprovedConstraintAnywhere_DoesNotQueryTheConstraintTable()
    {
        _presence.AnyApprovedAsync(Arg.Any<CancellationToken>()).Returns(false);

        var set = await _loader.LoadRuleSetAsync([_agentA], From, Until, null, 0, PlanningRuleSources.PlanningConstraints);

        set.Rules.ShouldBeEmpty();
        await _constraints.DidNotReceiveWithAnyArgs().GetApprovedForPeriodAsync(default, default, default, default);
    }

    [Test]
    public async Task InvalidConstraint_IsLoggedOncePerInterval_NotOnEveryLoad()
    {
        var invalid = Constraint(
            PlanningConstraintKind.MaxConsecutiveOfKind, PlanningConstraintScopeType.Global, null, """{"schemaVersion":9}""",
            PlanningConstraintSeverity.Soft);
        _constraints.GetApprovedForPeriodAsync(From, Until, null, Arg.Any<CancellationToken>()).Returns([invalid]);

        for (var load = 0; load < 3; load++)
        {
            await _loader.LoadRuleSetAsync([_agentA], From, Until, null, coveredBoundaryDays: 0);
        }

        _logger.ErrorCount.ShouldBe(1);
    }

    [TestCase("480", 480)]
    [TestCase("481", PlanningConstraintDefaults.DefaultNightRuleMinOverlapMinutes)]
    [TestCase(null, PlanningConstraintDefaults.DefaultNightRuleMinOverlapMinutes)]
    [TestCase("0", 0)]
    [TestCase("120", 120)]
    [TestCase("-5", PlanningConstraintDefaults.DefaultNightRuleMinOverlapMinutes)]
    [TestCase("abc", PlanningConstraintDefaults.DefaultNightRuleMinOverlapMinutes)]
    public async Task RuleSet_AgentsCarryTheNightRuleMinimumOverlapSetting(string? value, int expected)
    {
        _counterRules.GetAllApprovedAsync(Arg.Any<CancellationToken>()).Returns([Counter(null, null)]);
        _settings.GetSetting(SettingKeys.NightRuleMinOverlapMinutes).Returns(value is null
            ? null
            : new Klacks.Api.Domain.Models.Settings.Settings { Type = SettingKeys.NightRuleMinOverlapMinutes, Value = value });

        var set = await _loader.LoadRuleSetAsync([_agentA], From, Until, null, coveredBoundaryDays: 0);

        set.Agents.ShouldHaveSingleItem().NightRuleMinOverlapMinutes.ShouldBe(expected);
    }

    [Test]
    public async Task InvalidHardConstraint_ReportMode_IsListed_AndTheValidRulesStillLoad()
    {
        var invalid = Constraint(PlanningConstraintKind.MaxConsecutiveOfKind, PlanningConstraintScopeType.Global, null, """{"schemaVersion":9}""");
        var valid = Constraint(PlanningConstraintKind.MaxConsecutiveOfKind, PlanningConstraintScopeType.Global, null, MaxRunJson);
        _constraints.GetApprovedForPeriodAsync(From, Until, null, Arg.Any<CancellationToken>()).Returns([invalid, valid]);

        var set = await _loader.LoadRuleSetAsync(
            [_agentA], From, Until, null, 0, PlanningRuleSources.PlanningConstraints, InvalidHardRuleHandling.Report);

        set.Rules.ShouldHaveSingleItem().RuleId.ShouldBe(valid.Id);
        set.InvalidHardRuleIds.ShouldNotBeNull().ShouldBe([invalid.Id]);
    }

    [Test]
    public async Task GlobalCounterRule_HasNullScope_AndGlobalModeSeverity()
    {
        _enforcement.GetModeAsync(ComplianceRuleNames.CounterRule).Returns(RuleEnforcementMode.Block);
        _counterRules.GetAllApprovedAsync(Arg.Any<CancellationToken>()).Returns([Counter(null, null)]);

        var rule = (await _loader.LoadAsync([_agentA, _agentB], From, Until, null)).ShouldHaveSingleItem();

        var count = rule.ShouldBeOfType<PeriodCountRule>();
        count.AgentScope.ShouldBeNull();
        count.Severity.ShouldBe(RuleSeverity.Hard);
    }

    [Test]
    public async Task SchedulingRuleCounterRule_IsScopedToTheAgentsOfThatIndustry()
    {
        _counterRules.GetAllApprovedAsync(Arg.Any<CancellationToken>()).Returns([Counter(_industryRule, RuleEnforcementMode.Block)]);

        var rule = (await _loader.LoadAsync([_agentA, _agentB], From, Until, null)).ShouldHaveSingleItem();

        rule.AgentScope.ShouldBe(new HashSet<string> { _agentA.ToString() }, ignoreOrder: true);
        rule.Severity.ShouldBe(RuleSeverity.Hard);
    }

    [Test]
    public async Task ScopeResolvingToNoRequestedAgent_DropsTheRule_InsteadOfApplyingToEveryone()
    {
        _counterRules.GetAllApprovedAsync(Arg.Any<CancellationToken>()).Returns([Counter(Guid.NewGuid(), null)]);
        _constraints.GetApprovedForPeriodAsync(From, Until, null, Arg.Any<CancellationToken>()).Returns(
        [
            Constraint(PlanningConstraintKind.MaxConsecutiveOfKind, PlanningConstraintScopeType.Client, Guid.NewGuid(), MaxRunJson),
            Constraint(PlanningConstraintKind.TeamFairness, PlanningConstraintScopeType.Group, Guid.NewGuid(), FairnessJson,
                PlanningConstraintSeverity.Soft),
        ]);
        _groups.GetAllGroupIdsIncludingSubgroups(Arg.Any<Guid>()).Returns(new HashSet<Guid>());

        var rules = await _loader.LoadAsync([_agentA, _agentB], From, Until, null);

        rules.ShouldBeEmpty();
    }

    [Test]
    public async Task GroupScope_UnitesMembersOfTheGroupAndItsSubgroups()
    {
        var group = Guid.NewGuid();
        var subgroup = Guid.NewGuid();
        var token = Guid.NewGuid();
        _constraints.GetApprovedForPeriodAsync(From, Until, token, Arg.Any<CancellationToken>()).Returns(
        [
            Constraint(PlanningConstraintKind.TeamFairness, PlanningConstraintScopeType.Group, group, FairnessJson, PlanningConstraintSeverity.Soft),
        ]);
        _groups.GetAllGroupIdsIncludingSubgroups(group).Returns(new HashSet<Guid> { group, subgroup });
        _dataReader.GetGroupMembershipsAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Contains(group) && ids.Contains(subgroup)),
                Arg.Any<IReadOnlyCollection<Guid>>(), From, Until, token, Arg.Any<CancellationToken>())
            .Returns([new PlanningRuleGroupMembership(subgroup, _agentB)]);

        var rule = (await _loader.LoadAsync([_agentA, _agentB], From, Until, token)).ShouldHaveSingleItem();

        rule.ShouldBeOfType<TeamFairnessRule>().AgentScope.ShouldBe(new HashSet<string> { _agentB.ToString() }, ignoreOrder: true);
    }

    [Test]
    public async Task ClientAndGlobalConstraints_MapScopes()
    {
        _constraints.GetApprovedForPeriodAsync(From, Until, null, Arg.Any<CancellationToken>()).Returns(
        [
            Constraint(PlanningConstraintKind.MaxConsecutiveOfKind, PlanningConstraintScopeType.Client, _agentA, MaxRunJson),
            Constraint(PlanningConstraintKind.MaxConsecutiveOfKind, PlanningConstraintScopeType.Global, null, MaxRunJson),
        ]);

        var rules = await _loader.LoadAsync([_agentA, _agentB], From, Until, null);

        rules.Count.ShouldBe(2);
        rules[0].AgentScope.ShouldBe(new HashSet<string> { _agentA.ToString() }, ignoreOrder: true);
        rules[1].AgentScope.ShouldBeNull();
        rules.ShouldAllBe(r => r.Severity == RuleSeverity.Hard);
    }

    [Test]
    public async Task InvalidStoredHardConstraint_FailsClosed()
    {
        var invalid = Constraint(PlanningConstraintKind.MaxConsecutiveOfKind, PlanningConstraintScopeType.Global, null, """{"schemaVersion":9}""");
        _constraints.GetApprovedForPeriodAsync(From, Until, null, Arg.Any<CancellationToken>()).Returns([invalid]);

        var thrown = await Should.ThrowAsync<PlanningRuleConfigurationException>(() => _loader.LoadAsync([_agentA], From, Until, null));

        thrown.ConstraintId.ShouldBe(invalid.Id);
    }

    [Test]
    public async Task InvalidStoredSoftConstraint_IsSkipped_AndReported()
    {
        var invalid = Constraint(
            PlanningConstraintKind.MaxConsecutiveOfKind, PlanningConstraintScopeType.Global, null, """{"schemaVersion":9}""",
            PlanningConstraintSeverity.Soft);
        var valid = Constraint(PlanningConstraintKind.MaxConsecutiveOfKind, PlanningConstraintScopeType.Global, null, MaxRunJson);
        _constraints.GetApprovedForPeriodAsync(From, Until, null, Arg.Any<CancellationToken>()).Returns([invalid, valid]);

        var set = await _loader.LoadRuleSetAsync([_agentA], From, Until, null, coveredBoundaryDays: 0);

        set.Rules.ShouldHaveSingleItem().RuleId.ShouldBe(valid.Id);
        set.SkippedRuleIds.ShouldBe([invalid.Id]);
    }

    [Test]
    public async Task RuleSet_CarriesNightWindowAndWorkloadFromTheContract_AndPassesTheCoveredWindowOn()
    {
        _counterRules.GetAllApprovedAsync(Arg.Any<CancellationToken>()).Returns([Counter(null, null)]);

        var set = await _loader.LoadRuleSetAsync([_agentA, _agentB], From, Until, null, coveredBoundaryDays: 7);

        set.Agents.Count.ShouldBe(2);
        set.Agents[0].Id.ShouldBe(_agentA.ToString());
        set.Agents[0].NightWindow!.Value.Start.ShouldBe(new TimeOnly(22, 0));
        set.Agents[0].NightWindow!.Value.End.ShouldBe(new TimeOnly(5, 0));
        set.Agents[0].WorkloadPercent.ShouldBe(50m);
        set.Agents[1].NightWindow!.Value.Start.ShouldBe(TimeOnly.Parse(SurchargeDefaults.NightStart, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None));
        await _carryIn.Received(1).LoadAsync(
            Arg.Any<IReadOnlyCollection<Guid>>(), From, Until, set.Rules, null, 7, Arg.Any<CancellationToken>());
    }

    private static CounterRule Counter(Guid? schedulingRuleId, RuleEnforcementMode? enforcement) => new()
    {
        Id = Guid.NewGuid(),
        EventType = CounterEventType.NightShift,
        Period = CounterPeriod.Year,
        Threshold = 25,
        Enforcement = enforcement,
        SchedulingRuleId = schedulingRuleId,
    };

    private static PlanningConstraint Constraint(
        PlanningConstraintKind kind,
        PlanningConstraintScopeType scopeType,
        Guid? scopeId,
        string json,
        PlanningConstraintSeverity severity = PlanningConstraintSeverity.Hard) => new()
    {
        Id = Guid.NewGuid(),
        Kind = kind,
        Severity = severity,
        Weight = 1d,
        ScopeType = scopeType,
        ScopeId = scopeId,
        ParametersJson = json,
        ApprovalStatus = RuleApprovalStatus.Approved,
    };
}
