// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Covers the Prepare rung: which ledger statuses admit a preparation at all, that the scenario is
/// authored by Klacksy rather than left to the "Anonymous" audit fallback, that the ledger row ends up
/// pointing at it, that the notification is addressed per planner instead of broadcast (a broadcast
/// would open a ledger row nothing ever closes), and that a lost compare-and-swap takes the orphaned
/// scenario down with it. What is NOT proven here: the real ordering guarantee that the scenario is
/// COMMITTED before the ledger transition opens its own database transaction - the fakes have no
/// transactions to nest. That is proven against Postgres in
/// Klacks.IntegrationTest/Assistant/ConditionScenarioPreparationIntegrationTests.cs.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Conditions;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Constants;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Services.Assistant.Conditions;

[TestFixture]
public class ConditionScenarioPreparationServiceTests
{
    private const string TriggerKind = "empty_container";
    private static readonly DateOnly FromDate = new(2026, 9, 1);
    private static readonly DateOnly UntilDate = new(2026, 9, 7);

    private IAnalyseScenarioRepository _scenarioRepository = null!;
    private IAnalyseScenarioService _scenarioService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IAgentConditionLedgerService _ledgerService = null!;
    private IAgentTriggerService _triggerService = null!;
    private IPlanningAudienceResolver _audienceResolver = null!;
    private ConditionScenarioPreparationService _service = null!;
    private readonly List<AnalyseScenario> _added = new();

    [SetUp]
    public void SetUp()
    {
        _added.Clear();
        _scenarioRepository = Substitute.For<IAnalyseScenarioRepository>();
        _scenarioRepository.Add(Arg.Do<AnalyseScenario>(_added.Add)).Returns(Task.CompletedTask);
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _ledgerService = Substitute.For<IAgentConditionLedgerService>();
        _triggerService = Substitute.For<IAgentTriggerService>();
        _audienceResolver = Substitute.For<IPlanningAudienceResolver>();

        _service = new ConditionScenarioPreparationService(
            _scenarioRepository,
            _scenarioService,
            _unitOfWork,
            _ledgerService,
            _triggerService,
            _audienceResolver,
            TimeProvider.System,
            NullLogger<ConditionScenarioPreparationService>.Instance);
    }

    [Test]
    public async Task Prepare_ForAReportedCondition_CreatesAScenarioAuthoredByKlacksy()
    {
        // Arrange
        var condition = Condition(AgentConditionStatus.Reported);
        GivenTheLedgerAccepts();

        // Act
        var result = await _service.PrepareScenarioForConditionAsync(condition, Request());

        // Assert
        result.Outcome.ShouldBe(ConditionScenarioPreparationOutcome.Prepared);

        var scenario = _added.ShouldHaveSingleItem();
        scenario.CreatedByUser.ShouldBe(KlacksyIdentity.SystemUserName);
        scenario.GroupId.ShouldBe(condition.GroupId);
        scenario.FromDate.ShouldBe(FromDate);
        scenario.UntilDate.ShouldBe(UntilDate);
        scenario.Token.ShouldNotBe(Guid.Empty);
        scenario.RunGroupId.ShouldNotBeNull();
        result.ScenarioId.ShouldBe(scenario.Id);
        result.ScenarioToken.ShouldBe(scenario.Token);

        await _scenarioService.Received(1).CloneScenarioDataWithMapsAsync(
            condition.GroupId, FromDate, UntilDate, scenario.Token, null, Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Prepare_LinksTheScenarioOntoTheLedgerRowAndMovesItFromReportedToPrepared()
    {
        // Arrange
        var condition = Condition(AgentConditionStatus.Reported);
        GivenTheLedgerAccepts();

        // Act
        await _service.PrepareScenarioForConditionAsync(condition, Request());

        // Assert
        var scenario = _added.Single();
        await _ledgerService.Received(1).TryTransitionAsync(
            condition.Id,
            AgentConditionStatus.Reported,
            AgentConditionStatus.Prepared,
            Arg.Any<Guid?>(),
            Arg.Any<string?>(),
            Arg.Is<AgentConditionTransitionFields>(fields =>
                fields.ScenarioId == scenario.Id
                && fields.HandlingKind == AgentConditionHandlingKind.ScenarioPrepared
                && fields.HandledAtUtc != null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Prepare_NotifiesEachPlannerIndividually_SoNoSecondLedgerRowIsOpened()
    {
        // Arrange
        var condition = Condition(AgentConditionStatus.Reported);
        var firstPlanner = Guid.NewGuid();
        var secondPlanner = Guid.NewGuid();
        GivenTheLedgerAccepts();
        _audienceResolver
            .GetPlanningUserIdsForGroupAsync(condition.GroupId!.Value, Arg.Any<CancellationToken>())
            .Returns<IReadOnlySet<string>>(_ => new HashSet<string>
            {
                firstPlanner.ToString(), secondPlanner.ToString()
            });

        // Act
        await _service.PrepareScenarioForConditionAsync(condition, Request());

        // Assert
        var raised = _triggerService.ReceivedCalls()
            .Where(call => call.GetMethodInfo().Name == nameof(IAgentTriggerService.OnEventAsync))
            .Select(call => (IAgentTriggerEvent)call.GetArguments()[0]!)
            .ToList();

        raised.Count.ShouldBe(2);
        raised.ShouldAllBe(e => e is ScenarioPreparedTriggerEvent);
        raised.Select(e => e.TargetUserId).ShouldBe(new Guid?[] { firstPlanner, secondPlanner }, ignoreOrder: true);
        raised.ShouldAllBe(e => e.Kind == AgentTriggerKinds.ScenarioPrepared);

        // The audience gates are what AgentConditionLedgerPolicy.IsLedgerTracked keys on; a targeted
        // event is excluded from the ledger, which is the whole point of the per-planner shape.
        raised.ShouldAllBe(e => !e.PlannersOnly && !e.AdminOnly);
        raised.ShouldAllBe(e => AgentConditionLedgerPolicy.IsLedgerTracked(e) == false);
    }

    [Test]
    public async Task Prepare_ForAGrouplessCondition_UsesTheUnscopedPlanningAudience()
    {
        // Arrange
        var condition = Condition(AgentConditionStatus.Reported);
        condition.GroupId = null;
        GivenTheLedgerAccepts();
        _audienceResolver.GetPlanningUserIdsAsync(Arg.Any<CancellationToken>())
            .Returns<IReadOnlySet<string>>(_ => new HashSet<string> { Guid.NewGuid().ToString() });

        // Act
        await _service.PrepareScenarioForConditionAsync(condition, Request());

        // Assert
        await _audienceResolver.Received(1).GetPlanningUserIdsAsync(Arg.Any<CancellationToken>());
        await _audienceResolver.DidNotReceiveWithAnyArgs()
            .GetPlanningUserIdsForGroupAsync(default, default);
    }

    [Test]
    public async Task Prepare_WhenAlreadyPrepared_ReportsTheExistingScenarioAndCreatesNoSecondOne()
    {
        // Arrange
        var condition = Condition(AgentConditionStatus.Prepared);
        condition.ScenarioId = Guid.NewGuid();

        // Act
        var result = await _service.PrepareScenarioForConditionAsync(condition, Request());

        // Assert
        result.Outcome.ShouldBe(ConditionScenarioPreparationOutcome.AlreadyPrepared);
        result.ScenarioId.ShouldBe(condition.ScenarioId);
        _added.ShouldBeEmpty();
        await _ledgerService.DidNotReceiveWithAnyArgs().TryTransitionAsync(
            default, default, default, default, default, default, default);
    }

    [TestCase(AgentConditionStatus.Detected)]
    [TestCase(AgentConditionStatus.Executed)]
    [TestCase(AgentConditionStatus.Rejected)]
    [TestCase(AgentConditionStatus.Resolved)]
    [TestCase(AgentConditionStatus.Escalated)]
    public async Task Prepare_FromAStatusTheStateMachineDoesNotAdmit_ChangesNothing(AgentConditionStatus status)
    {
        // Arrange - Detected included on purpose: Prepared is reachable only from Reported, so going
        // straight from Detected would throw inside the ledger service rather than return false.
        AgentConditionStateMachine
            .IsLegalTransition(status, AgentConditionStatus.Prepared)
            .ShouldBeFalse();

        // Act
        var result = await _service.PrepareScenarioForConditionAsync(Condition(status), Request());

        // Assert
        result.Outcome.ShouldBe(ConditionScenarioPreparationOutcome.NotPreparable);
        result.ScenarioId.ShouldBeNull();
        _added.ShouldBeEmpty();
        await _scenarioService.DidNotReceiveWithAnyArgs().CloneScenarioDataWithMapsAsync(
            default, default, default, default, default, default);
    }

    [Test]
    public async Task Prepare_WhenAnotherInstanceWinsTheTransition_DiscardsItsOwnOrphanedScenario()
    {
        // Arrange
        var condition = Condition(AgentConditionStatus.Reported);
        _ledgerService.TryTransitionAsync(
            Arg.Any<Guid>(), Arg.Any<AgentConditionStatus>(), Arg.Any<AgentConditionStatus>(),
            Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<AgentConditionTransitionFields?>(),
            Arg.Any<CancellationToken>()).Returns(false);

        // Act
        var result = await _service.PrepareScenarioForConditionAsync(condition, Request());

        // Assert
        result.Outcome.ShouldBe(ConditionScenarioPreparationOutcome.LedgerConflict);
        result.ScenarioId.ShouldBeNull();

        var scenario = _added.Single();
        await _scenarioService.Received(1).SoftDeleteScenarioDataAsync(scenario.Token, Arg.Any<CancellationToken>());
        await _scenarioRepository.Received(1).Delete(scenario.Id);
        await _triggerService.DidNotReceiveWithAnyArgs().OnEventAsync(default!, default);
    }

    [Test]
    public async Task Prepare_WhenTheNotificationFails_KeepsThePreparationRatherThanUndoingIt()
    {
        // Arrange
        var condition = Condition(AgentConditionStatus.Reported);
        GivenTheLedgerAccepts();
        _audienceResolver.GetPlanningUserIdsForGroupAsync(condition.GroupId!.Value, Arg.Any<CancellationToken>())
            .Returns<IReadOnlySet<string>>(_ => throw new InvalidOperationException("audience unavailable"));

        // Act
        var result = await _service.PrepareScenarioForConditionAsync(condition, Request());

        // Assert
        result.Outcome.ShouldBe(ConditionScenarioPreparationOutcome.Prepared);
        await _scenarioService.DidNotReceiveWithAnyArgs().SoftDeleteScenarioDataAsync(default, default);
    }

    [Test]
    public async Task Prepare_WithAnExplicitNameAndGroup_PrefersThemOverTheConditionsOwn()
    {
        // Arrange
        var condition = Condition(AgentConditionStatus.Reported);
        var overrideGroupId = Guid.NewGuid();
        const string overrideName = "Container fix week 36";
        GivenTheLedgerAccepts();

        // Act
        await _service.PrepareScenarioForConditionAsync(
            condition, new ConditionScenarioRequest(FromDate, UntilDate, overrideName, overrideGroupId));

        // Assert
        var scenario = _added.Single();
        scenario.Name.ShouldBe(overrideName);
        scenario.GroupId.ShouldBe(overrideGroupId);
    }

    [Test]
    public async Task Prepare_WithoutAName_NamesTheScenarioAfterKlacksyAndTheFinding()
    {
        // Arrange
        var condition = Condition(AgentConditionStatus.Reported);
        GivenTheLedgerAccepts();

        // Act
        await _service.PrepareScenarioForConditionAsync(condition, Request());

        // Assert
        var name = _added.Single().Name;
        name.ShouldContain(KlacksyIdentity.SystemUserName);
        name.ShouldContain(TriggerKind);
    }

    private void GivenTheLedgerAccepts()
    {
        _ledgerService.TryTransitionAsync(
            Arg.Any<Guid>(), Arg.Any<AgentConditionStatus>(), Arg.Any<AgentConditionStatus>(),
            Arg.Any<Guid?>(), Arg.Any<string?>(), Arg.Any<AgentConditionTransitionFields?>(),
            Arg.Any<CancellationToken>()).Returns(true);
    }

    private static ConditionScenarioRequest Request() => new(FromDate, UntilDate);

    private static AgentCondition Condition(AgentConditionStatus status) => new()
    {
        Id = Guid.NewGuid(),
        TriggerKind = TriggerKind,
        Fingerprint = TriggerKind + ":" + Guid.NewGuid(),
        GroupId = Guid.NewGuid(),
        Severity = AgentTriggerSeverity.Medium,
        Status = status,
        DetectedAtUtc = DateTime.UtcNow,
        LastSeenAtUtc = DateTime.UtcNow,
        PayloadJson = "{}"
    };
}
