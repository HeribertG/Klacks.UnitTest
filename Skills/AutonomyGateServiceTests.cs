// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AutonomyGateServiceTests
{
    private IAgentAutonomyPreferenceRepository _preferenceRepository = null!;
    private ISkillRiskClassifier _riskClassifier = null!;
    private IPendingConfirmationStore _confirmationStore = null!;
    private TurnConfirmationScope _turnScope = null!;
    private AutonomyGateService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _preferenceRepository = Substitute.For<IAgentAutonomyPreferenceRepository>();
        _riskClassifier = Substitute.For<ISkillRiskClassifier>();
        _confirmationStore = PendingStoreTestFactory.CreateConfirmationStore();
        _turnScope = new TurnConfirmationScope();
        _sut = CreateGate(_turnScope);
    }

    private AutonomyGateService CreateGate(TurnConfirmationScope turnScope) => new(
        _preferenceRepository,
        _riskClassifier,
        _confirmationStore,
        turnScope,
        NullLogger<AutonomyGateService>.Instance);

    private static SkillDescriptor Descriptor(string name = "test_skill")
        => new(name, "test", SkillCategory.Crud, [], [], [], null);

    private static SkillExecutionContext Context(Guid? userId = null, bool bypass = false) => new()
    {
        UserId = userId ?? Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>(),
        BypassAutonomyGate = bypass
    };

    private void SetLevel(Guid userId, AutonomyLevel level)
    {
        _preferenceRepository.GetAsync(userId.ToString(), Arg.Any<CancellationToken>())
            .Returns(new AgentAutonomyPreferenceRow { UserId = userId.ToString(), Level = level });
    }

    private void SetRisk(SkillRiskClass riskClass)
    {
        _riskClassifier.Classify(Arg.Any<SkillDescriptor>()).Returns(riskClass);
    }

    [Test]
    public async Task Check_ReadOnlySkill_AlwaysAllowed_EvenAtProposeLevel()
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.ReadOnly);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        Assert.That(result, Is.Null);
    }

    [TestCase(SkillRiskClass.Reversible)]
    [TestCase(SkillRiskClass.ScenarioGated)]
    [TestCase(SkillRiskClass.Irreversible)]
    [TestCase(SkillRiskClass.Sensitive)]
    public async Task Check_ProposeLevel_HoldsAllWriters(SkillRiskClass riskClass)
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(riskClass);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Type, Is.EqualTo(SkillResultType.Confirmation));
        Assert.That(result.Metadata, Does.ContainKey("confirmationToken"));
    }

    [TestCase(SkillRiskClass.Reversible, true)]
    [TestCase(SkillRiskClass.ScenarioGated, true)]
    [TestCase(SkillRiskClass.Irreversible, false)]
    [TestCase(SkillRiskClass.Sensitive, false)]
    public async Task Check_AssistedLevel_AllowsReversibleAndScenarioGatedOnly(SkillRiskClass riskClass, bool allowed)
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Assisted);
        SetRisk(riskClass);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        Assert.That(result == null, Is.EqualTo(allowed));
    }

    [TestCase(SkillRiskClass.Reversible, true)]
    [TestCase(SkillRiskClass.ScenarioGated, true)]
    [TestCase(SkillRiskClass.Irreversible, true)]
    [TestCase(SkillRiskClass.Sensitive, false)]
    public async Task Check_AutonomousLevel_AllowsEverythingExceptSensitive(SkillRiskClass riskClass, bool allowed)
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Autonomous);
        SetRisk(riskClass);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        Assert.That(result == null, Is.EqualTo(allowed));
    }

    [Test]
    public async Task Check_SensitiveSkill_HeldEvenAtFullyAutonomous()
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.FullyAutonomous);
        SetRisk(SkillRiskClass.Sensitive);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        Assert.That(result, Is.Not.Null);
        Assert.That(result!.Type, Is.EqualTo(SkillResultType.Confirmation));
    }

    [Test]
    public async Task Check_NoPreferenceRow_UsesDefaultLevel_AllowsIrreversible()
    {
        var context = Context();
        _preferenceRepository.GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentAutonomyPreferenceRow?)null);
        SetRisk(SkillRiskClass.Irreversible);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        Assert.That(result, Is.Null);
    }

    [Test]
    public async Task Check_ValidConfirmationToken_AllowsAndStripsParameter()
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Irreversible);
        var descriptor = Descriptor("delete_shift");
        var token = _confirmationStore.Create(
            context.UserId, "delete_shift", new Dictionary<string, object> { ["id"] = "x" });
        var parameters = new Dictionary<string, object>
        {
            ["id"] = "x",
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        };

        var result = await _sut.CheckAsync(descriptor, context, parameters);

        Assert.That(result, Is.Null);
        Assert.That(parameters, Does.Not.ContainKey(AutonomyDefaults.ConfirmationTokenParameter));
    }

    [Test]
    public async Task Check_SensitiveToken_SameTurnRedemption_HeldAgain_NextTurnAllowed()
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.FullyAutonomous);
        SetRisk(SkillRiskClass.Sensitive);
        var descriptor = Descriptor("close_period");
        var parameters = new Dictionary<string, object> { ["groupName"] = "Group A" };

        var hold = await _sut.CheckAsync(descriptor, context, new Dictionary<string, object>(parameters));
        var token = (string)hold!.Metadata!["confirmationToken"];

        var sameTurn = await _sut.CheckAsync(descriptor, context, new Dictionary<string, object>(parameters)
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });
        var nextTurnGate = CreateGate(new TurnConfirmationScope());
        var nextTurn = await nextTurnGate.CheckAsync(descriptor, context, new Dictionary<string, object>(parameters)
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(sameTurn, Is.Not.Null);
        Assert.That(nextTurn, Is.Null);
    }

    [Test]
    public async Task Check_TokenWithDifferentParameters_HeldAgain()
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Sensitive);
        var descriptor = Descriptor("close_period");
        var token = _confirmationStore.Create(
            context.UserId, "close_period", new Dictionary<string, object> { ["groupName"] = "Group A" });

        var result = await _sut.CheckAsync(descriptor, context, new Dictionary<string, object>
        {
            ["groupName"] = "Group B",
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(result, Is.Not.Null);
        Assert.That((string)result!.Metadata!["confirmationToken"], Is.Not.EqualTo(token));
    }

    [Test]
    public async Task Check_TokenWithDifferentParameters_IsBurned_SameActionHeldAfterwards()
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Sensitive);
        var descriptor = Descriptor("close_period");
        var storedParameters = new Dictionary<string, object> { ["groupName"] = "Group A" };
        var token = _confirmationStore.Create(context.UserId, "close_period", storedParameters);

        await _sut.CheckAsync(descriptor, context, new Dictionary<string, object>
        {
            ["groupName"] = "Group B",
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });
        var replayWithOriginalParameters = await _sut.CheckAsync(descriptor, context, new Dictionary<string, object>
        {
            ["groupName"] = "Group A",
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(replayWithOriginalParameters, Is.Not.Null);
    }

    [Test]
    public async Task Check_TokenIsSingleUse_SecondCallHeldAgain()
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Irreversible);
        var descriptor = Descriptor("delete_shift");
        var token = _confirmationStore.Create(context.UserId, "delete_shift", new Dictionary<string, object>());

        var first = await _sut.CheckAsync(descriptor, context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });
        var second = await _sut.CheckAsync(descriptor, context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(first, Is.Null);
        Assert.That(second, Is.Not.Null);
    }

    [Test]
    public async Task Check_TokenForOtherSkill_Held()
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Irreversible);
        var token = _confirmationStore.Create(context.UserId, "delete_shift", new Dictionary<string, object>());

        var result = await _sut.CheckAsync(Descriptor("delete_group"), context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task Check_TokenForOtherUser_Held()
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Irreversible);
        var token = _confirmationStore.Create(Guid.NewGuid(), "delete_shift", new Dictionary<string, object>());

        var result = await _sut.CheckAsync(Descriptor("delete_shift"), context, new Dictionary<string, object>
        {
            [AutonomyDefaults.ConfirmationTokenParameter] = token
        });

        Assert.That(result, Is.Not.Null);
    }

    [Test]
    public async Task Check_ConfirmationMessage_ContainsReplayableToken()
    {
        var context = Context();
        SetLevel(context.UserId, AutonomyLevel.Propose);
        SetRisk(SkillRiskClass.Irreversible);

        var result = await _sut.CheckAsync(Descriptor("delete_shift"), context, new Dictionary<string, object>());

        Assert.That(result, Is.Not.Null);
        var token = (string)result!.Metadata!["confirmationToken"];
        Assert.That(result.Message, Does.Contain(token));
        Assert.That(result.Message, Does.Contain(AutonomyDefaults.ConfirmationTokenParameter));
    }

    [Test]
    public async Task Check_BypassContext_SkipsGateEntirely()
    {
        var context = Context(bypass: true);
        SetRisk(SkillRiskClass.Sensitive);

        var result = await _sut.CheckAsync(Descriptor(), context, new Dictionary<string, object>());

        Assert.That(result, Is.Null);
        await _preferenceRepository.DidNotReceive().GetAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
