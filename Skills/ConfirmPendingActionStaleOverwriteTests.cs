// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The stale-scenario consent travels only on the context: confirm_pending_action sets
/// StaleScenarioOverwriteAcknowledgement exclusively for a StaleScenarioOverwrite token of accept_scenario, from the
/// staleness stored with that token, and removes the storage keys from the replayed parameters. A gate-replay token
/// that happens to carry the same keys (the gate stores LLM arguments verbatim) yields no consent.
/// </summary>

using System.Globalization;
using Klacks.Api.Application.Services.Assistant.Autonomy;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ConfirmPendingActionStaleOverwriteTests
{
    private static readonly DateTime LastChangeUtc = new(2026, 4, 20, 10, 0, 0, DateTimeKind.Utc);

    private IPendingConfirmationStore _store = null!;
    private ISkillExecutor _skillExecutor = null!;
    private ConfirmPendingActionSkill _sut = null!;

    [SetUp]
    public void Setup()
    {
        _store = PendingStoreTestFactory.CreateConfirmationStore();
        _skillExecutor = Substitute.For<ISkillExecutor>();
        _skillExecutor.ExecuteAsync(Arg.Any<SkillInvocation>(), Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(SkillResult.SuccessResult(null, "done"));
        _sut = new ConfirmPendingActionSkill(_store, _skillExecutor, new ConfirmationRedemptionGuard(new TurnConfirmationScope()));
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>(),
        UserConfirmation = UserConfirmationEvidence.ExplicitConfirmationReply
    };

    private static Dictionary<string, object> StoredParameters() => new()
    {
        ["scenarioId"] = Guid.NewGuid().ToString(),
        [ScenarioAcceptDefaults.AcknowledgedChangedEntryCountParameter] = "4",
        [ScenarioAcceptDefaults.AcknowledgedLastChangeUtcTicksParameter] =
            LastChangeUtc.Ticks.ToString(CultureInfo.InvariantCulture)
    };

    private Task<SkillResult> RedeemAsync(SkillExecutionContext context, string token) =>
        _sut.ExecuteAsync(context, new Dictionary<string, object> { [AutonomyDefaults.ConfirmationTokenParameter] = token });

    [Test]
    public async Task StaleOverwriteToken_SetsTheAcknowledgementOnTheContext_AndStripsTheStorageKeys()
    {
        var context = Ctx();
        var token = _store.Create(
            context.UserId, ScenarioAcceptDefaults.SkillName, StoredParameters(),
            PendingConfirmationPurposes.StaleScenarioOverwrite);

        var result = await RedeemAsync(context, token);

        result.Success.ShouldBeTrue();
        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Is<SkillInvocation>(i => i.SkillName == ScenarioAcceptDefaults.SkillName
                && i.Parameters.ContainsKey("scenarioId")
                && !i.Parameters.ContainsKey(ScenarioAcceptDefaults.AcknowledgedChangedEntryCountParameter)
                && !i.Parameters.ContainsKey(ScenarioAcceptDefaults.AcknowledgedLastChangeUtcTicksParameter)),
            Arg.Is<SkillExecutionContext>(c => c.BypassAutonomyGate
                && c.StaleScenarioOverwriteAcknowledgement != null
                && c.StaleScenarioOverwriteAcknowledgement.ChangedEntryCount == 4
                && c.StaleScenarioOverwriteAcknowledgement.LastChangeUtc == LastChangeUtc
                && c.StaleScenarioOverwriteAcknowledgement.LastChangeUtc.Kind == DateTimeKind.Utc),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GateReplayTokenCarryingTheSameKeys_GivesNoAcknowledgement()
    {
        var context = Ctx();
        var token = _store.Create(context.UserId, ScenarioAcceptDefaults.SkillName, StoredParameters());

        await RedeemAsync(context, token);

        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Any<SkillInvocation>(),
            Arg.Is<SkillExecutionContext>(c => c.StaleScenarioOverwriteAcknowledgement == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task StaleOverwriteTokenOfAnotherSkill_GivesNoAcknowledgement()
    {
        var context = Ctx();
        var token = _store.Create(
            context.UserId, "delete_shift", StoredParameters(), PendingConfirmationPurposes.StaleScenarioOverwrite);

        await RedeemAsync(context, token);

        await _skillExecutor.Received(1).ExecuteAsync(
            Arg.Any<SkillInvocation>(),
            Arg.Is<SkillExecutionContext>(c => c.StaleScenarioOverwriteAcknowledgement == null),
            Arg.Any<CancellationToken>());
    }
}
