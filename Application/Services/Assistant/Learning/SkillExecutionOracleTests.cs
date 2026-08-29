// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for oracle O2. Its job is to stop a composition that must never exist and to prove one that
/// may, and the two failure modes are asymmetric: activating an unsafe capability writes into the one
/// live database, while rejecting a safe one costs a learning round. So the boundary cases are checked
/// from both sides - everything the risk classifier does not place in ReadOnly or Reversible is refused,
/// and nothing that writes is ever executed to find out.
/// The distinction between Rejected and Inconclusive is tested as its own concern, because collapsing
/// the two would let an unavailable identity declare a wish unservable.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Learning;

using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillExecutionOracleTests
{
    private const string ReadSkill = "list_clients";
    private const string SecondReadSkill = "get_current_time";
    private const string WriteSkill = "add_client_to_group";
    private const string OwnerId = "3f2504e0-4f89-11d3-9a0c-0305e82c3301";

    private ISkillRegistry _registry = null!;
    private ISkillRiskClassifier _classifier = null!;
    private IProactiveActionIdentityProvider _identityProvider = null!;
    private ISkillExecutor _executor = null!;
    private SkillExecutionOracle _oracle = null!;

    [SetUp]
    public void SetUp()
    {
        _registry = Substitute.For<ISkillRegistry>();
        _classifier = Substitute.For<ISkillRiskClassifier>();
        _identityProvider = Substitute.For<IProactiveActionIdentityProvider>();
        _executor = Substitute.For<ISkillExecutor>();

        GivenSkill(ReadSkill, SkillRiskClass.ReadOnly);
        GivenSkill(SecondReadSkill, SkillRiskClass.ReadOnly);
        GivenSkill(WriteSkill, SkillRiskClass.Reversible);
        GivenIdentity();
        GivenExecutionResult(SkillResult.SuccessResult(new { ok = true }));

        _oracle = new SkillExecutionOracle(
            _registry, _classifier, _identityProvider, _executor,
            Substitute.For<ILogger<SkillExecutionOracle>>());
    }

    [Test]
    public async Task AChainOfReadOnlyStepsWithConstantParameters_RunsAndPasses()
    {
        var probe = await _oracle.ProbeAsync(
            [Step(ReadSkill), Step(SecondReadSkill)], OwnerId, Guid.NewGuid());

        probe.Verdict.ShouldBe(SkillExecutionVerdict.Passed);
        probe.FullyExecuted.ShouldBeTrue();
        probe.Steps.Count.ShouldBe(2);
        probe.Steps.ShouldAllBe(step => step.Executed && step.Success);
        await _executor.Received(2).ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    // The whole point of the V1 boundary: there is no rollback and no test tenant, so a step that writes
    // is checked and left alone. Passing while owing a first real use is the honest verdict.
    [Test]
    public async Task AReversibleStep_IsNeverExecutedAndLeavesTheCapabilityOwingItsFirstUse()
    {
        var probe = await _oracle.ProbeAsync([Step(WriteSkill)], OwnerId, Guid.NewGuid());

        probe.Verdict.ShouldBe(SkillExecutionVerdict.Passed);
        probe.FullyExecuted.ShouldBeFalse();
        probe.Steps.Single().Executed.ShouldBeFalse();
        await _executor.DidNotReceive().ExecuteAsync(
            Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AReadOnlyStepAfterAWritingOne_IsNotExecutedEither()
    {
        var probe = await _oracle.ProbeAsync(
            [Step(WriteSkill), Step(ReadSkill)], OwnerId, Guid.NewGuid());

        probe.Verdict.ShouldBe(SkillExecutionVerdict.Passed);
        probe.FullyExecuted.ShouldBeFalse();
        probe.Steps.ShouldAllBe(step => !step.Executed);
    }

    [TestCase(SkillRiskClass.Sensitive)]
    [TestCase(SkillRiskClass.Irreversible)]
    [TestCase(SkillRiskClass.ScenarioGated)]
    public async Task ASkillOutsideReadOnlyAndReversible_IsRefused(SkillRiskClass riskClass)
    {
        GivenSkill(ReadSkill, riskClass);

        var probe = await _oracle.ProbeAsync([Step(ReadSkill)], OwnerId, Guid.NewGuid());

        probe.Verdict.ShouldBe(SkillExecutionVerdict.Rejected);
        probe.Error.ShouldContain(riskClass.ToString());
    }

    // The classifier returns Sensitive both for a listed skill and for one it cannot place, so an
    // unknown skill is caught one step earlier - by the registry, which is the only thing that can tell
    // "does not exist" from "exists and is dangerous".
    [Test]
    public async Task ASkillTheRegistryDoesNotKnow_IsRefused()
    {
        _registry.GetSkillByName("invented_skill").Returns((SkillDescriptor?)null);

        var probe = await _oracle.ProbeAsync([Step("invented_skill")], OwnerId, Guid.NewGuid());

        probe.Verdict.ShouldBe(SkillExecutionVerdict.Rejected);
        probe.Error.ShouldContain("does not exist");
    }

    [Test]
    public async Task ABrowserActionStep_IsRefused()
    {
        GivenSkill(ReadSkill, SkillRiskClass.ReadOnly, LlmExecutionTypes.UiAction);

        var probe = await _oracle.ProbeAsync([Step(ReadSkill)], OwnerId, Guid.NewGuid());

        probe.Verdict.ShouldBe(SkillExecutionVerdict.Rejected);
        probe.Error.ShouldContain("browser action");
    }

    [Test]
    public async Task AQuestionStep_IsRefusedBecauseItsAnswerWouldHaveToBeInvented()
    {
        var probe = await _oracle.ProbeAsync(
            [new RecipeStep { Kind = RecipeStepKinds.Ask, Slot = "groupName", Prompt = "Which group?" }],
            OwnerId,
            Guid.NewGuid());

        probe.Verdict.ShouldBe(SkillExecutionVerdict.Rejected);
        probe.Error.ShouldContain(RecipeStepKinds.Ask);
    }

    [Test]
    public async Task ARequiredParameterNothingBinds_IsRefused()
    {
        GivenSkill(ReadSkill, SkillRiskClass.ReadOnly, parameters: [Required("groupName")]);

        var probe = await _oracle.ProbeAsync([Step(ReadSkill)], OwnerId, Guid.NewGuid());

        probe.Verdict.ShouldBe(SkillExecutionVerdict.Rejected);
        probe.Error.ShouldContain("groupName");
    }

    [Test]
    public async Task ARequiredParameterBoundToAConstant_IsAccepted()
    {
        GivenSkill(ReadSkill, SkillRiskClass.ReadOnly, parameters: [Required("groupName")]);

        var step = Step(ReadSkill);
        step.Inject = new Dictionary<string, string> { ["groupName"] = "Nachtdienst" };

        var probe = await _oracle.ProbeAsync([step], OwnerId, Guid.NewGuid());

        probe.Verdict.ShouldBe(SkillExecutionVerdict.Passed);
    }

    [Test]
    public async Task ASlotReferenceNoEarlierStepProduces_IsRefused()
    {
        var step = Step(ReadSkill);
        step.Inject = new Dictionary<string, string> { ["clientId"] = "$clientId" };

        var probe = await _oracle.ProbeAsync([step], OwnerId, Guid.NewGuid());

        probe.Verdict.ShouldBe(SkillExecutionVerdict.Rejected);
        probe.Error.ShouldContain("$clientId");
    }

    // A captured value only exists once the producing step has really run, so the consuming step is
    // accepted statically but never executed against a value the probe would have to guess.
    [Test]
    public async Task ASlotReferenceAnEarlierStepCaptures_IsAcceptedButNotExecuted()
    {
        var producer = Step(ReadSkill);
        producer.Capture = "clients[].id as clientId";

        var consumer = Step(SecondReadSkill);
        consumer.Inject = new Dictionary<string, string> { ["clientId"] = "$clientId" };

        var probe = await _oracle.ProbeAsync([producer, consumer], OwnerId, Guid.NewGuid());

        probe.Verdict.ShouldBe(SkillExecutionVerdict.Passed);
        probe.FullyExecuted.ShouldBeFalse();
        probe.Steps[0].Executed.ShouldBeTrue();
        probe.Steps[1].Executed.ShouldBeFalse();
    }

    [Test]
    public async Task AReadOnlyStepThatFails_RejectsTheWholeComposition()
    {
        GivenExecutionResult(SkillResult.Error("The group does not exist."));

        var probe = await _oracle.ProbeAsync([Step(ReadSkill)], OwnerId, Guid.NewGuid());

        probe.Verdict.ShouldBe(SkillExecutionVerdict.Rejected);
        probe.Error.ShouldContain("The group does not exist.");
    }

    [Test]
    public async Task AStepThatAsksForConfirmation_RejectsTheComposition()
    {
        GivenExecutionResult(SkillResult.Confirmation("Really?", "token"));

        var probe = await _oracle.ProbeAsync([Step(ReadSkill)], OwnerId, Guid.NewGuid());

        probe.Verdict.ShouldBe(SkillExecutionVerdict.Rejected);
        probe.Steps.Single().Error.ShouldContain("confirmation");
    }

    [Test]
    public async Task AThrowingStep_RejectsTheCompositionRatherThanTheRun()
    {
        _executor
            .ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns<Task<SkillResult>>(_ => throw new InvalidOperationException("connection reset"));

        var probe = await _oracle.ProbeAsync([Step(ReadSkill)], OwnerId, Guid.NewGuid());

        probe.Verdict.ShouldBe(SkillExecutionVerdict.Rejected);
        probe.Error.ShouldContain("connection reset");
    }

    // An outage is not evidence about the wish. Reporting it as Rejected would spend an attempt and
    // could declare a perfectly serviceable composition unservable.
    [Test]
    public async Task AnUnavailableIdentity_IsInconclusiveRatherThanRejected()
    {
        _identityProvider
            .ResolveForSkillAsync(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProactiveActionIdentity.Refused(
                ProactiveActionIdentityRefusal.TokenRefused, "The owner's token was refused."));

        var probe = await _oracle.ProbeAsync([Step(ReadSkill)], OwnerId, Guid.NewGuid());

        probe.Verdict.ShouldBe(SkillExecutionVerdict.Inconclusive);
        probe.Error.ShouldContain("token was refused");
    }

    // Nothing has to run, so nothing has to be minted: a composition of writes alone never touches the
    // identity provider and therefore cannot be held up by it.
    [Test]
    public async Task ACompositionWithNothingToRun_NeedsNoIdentityAtAll()
    {
        var probe = await _oracle.ProbeAsync([Step(WriteSkill)], OwnerId, Guid.NewGuid());

        probe.Verdict.ShouldBe(SkillExecutionVerdict.Passed);
        await _identityProvider.DidNotReceive().ResolveForSkillAsync(
            Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    // The provider mints its context for the unattended action path, which bypasses the gate. A probe
    // that inherited that would prove an execution no real user will ever get.
    [Test]
    public async Task TheProbe_RunsWithTheAutonomyGateOnAndWithoutBrowserSupport()
    {
        await _oracle.ProbeAsync([Step(ReadSkill)], OwnerId, Guid.NewGuid());

        await _executor.Received(1).ExecuteAsync(
            Arg.Any<SkillInvocation>(),
            Arg.Is<SkillExecutionContext>(c => !c.BypassAutonomyGate && !c.SupportsUiActions),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnEmptyComposition_IsRefused()
    {
        var probe = await _oracle.ProbeAsync([], OwnerId, Guid.NewGuid());

        probe.Verdict.ShouldBe(SkillExecutionVerdict.Rejected);
        probe.Error.ShouldContain("at least one step");
    }

    [Test]
    public async Task ACompositionLongerThanTheCap_IsRefused()
    {
        var steps = Enumerable
            .Range(0, SkillLearningDefaults.MaxCapabilityStepCount + 1)
            .Select(_ => Step(ReadSkill))
            .ToList();

        var probe = await _oracle.ProbeAsync(steps, OwnerId, Guid.NewGuid());

        probe.Verdict.ShouldBe(SkillExecutionVerdict.Rejected);
        probe.Error.ShouldContain("at most");
    }

    private static RecipeStep Step(string skill) =>
        new() { Kind = RecipeStepKinds.Search, Skill = skill };

    private static SkillParameter Required(string name) =>
        new(name, name, SkillParameterType.String, true);

    private void GivenSkill(
        string name,
        SkillRiskClass riskClass,
        string executionType = LlmExecutionTypes.Skill,
        IReadOnlyList<SkillParameter>? parameters = null)
    {
        var descriptor = new SkillDescriptor(
            name, name, SkillCategory.Query, parameters ?? [], [], [], null)
        {
            ExecutionType = executionType
        };

        _registry.GetSkillByName(name).Returns(descriptor);
        _classifier.Classify(Arg.Is<SkillDescriptor>(d => d.Name == name)).Returns(riskClass);
    }

    private void GivenIdentity() =>
        _identityProvider
            .ResolveForSkillAsync(Arg.Any<Guid?>(), Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ProactiveActionIdentity.Resolved(
                new SkillExecutionContext
                {
                    UserId = Guid.Parse(OwnerId),
                    TenantId = Guid.Empty,
                    UserName = KlacksyIdentity.SystemUserName,
                    UserPermissions = [Roles.Admin],
                    BypassAutonomyGate = true
                },
                [Roles.Admin]));

    private void GivenExecutionResult(SkillResult result) =>
        _executor
            .ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(result);
}
