// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for LLMService.ResolvePendingConfirmation, the seam that resurfaces an outstanding
/// autonomy-gate confirmation token in the turn after the gate asked for it. The token itself never
/// survives in the conversation history (only user/assistant text is persisted), so this is the only
/// path by which a held sensitive action can ever be confirmed. The load-bearing case is a reply that
/// affirms AND restates the mutation ("ja, lösch den Benutzer"): it must still resurface the token,
/// because a mutation-intent veto there made the model re-call the skill, which produced a fresh hold
/// and left the user confirming the same action over and over.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class LLMServicePendingConfirmationForceTests
{
    private const string PendingSkillName = "delete_system_user";
    private const string PendingToken = "token-abc";

    private static readonly Guid UserId = Guid.NewGuid();

    private IPendingConfirmationStore _confirmationStore = null!;
    private LLMService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _confirmationStore = Substitute.For<IPendingConfirmationStore>();

        // Every other dependency is untouched by ResolvePendingConfirmation, which reads only the
        // context, the pending-confirmation store and AutonomyDefaults.
        _service = new LLMService(
            logger: Substitute.For<ILogger<LLMService>>(),
            providerOrchestrator: null!,
            conversationManager: null!,
            functionExecutor: null!,
            responseBuilder: null!,
            promptBuilder: null!,
            agentRepository: null!,
            contextAssemblyPipeline: null!,
            backgroundTaskService: null!,
            pendingConfirmationStore: _confirmationStore,
            recipeEngine: null!,
            recipeRunRecorder: Substitute.For<IRecipeRunRecorder>(),
            slotExtractor: null!,
            suggestionEntityNameReader: null!,
            contextBudgetPolicy: null!);
    }

    private void SetPending() =>
        _confirmationStore
            .PeekLatestForUser(UserId, Arg.Any<TimeSpan>(), Arg.Any<string>())
            .Returns(new PendingConfirmationHandle(PendingToken, PendingSkillName));

    private static LLMContext Context(string message) => new()
    {
        Message = message,
        UserId = UserId.ToString(),
        AvailableFunctions =
        [
            new LLMFunction { Name = AutonomyDefaults.ConfirmPendingActionSkillName }
        ]
    };

    // The regression case: the reply confirms AND names the mutation again. Both detectors fire, and
    // the pending gate-replay row has to win, or the confirmation can never be answered.
    [TestCase("ja, lösch den Benutzer")]
    [TestCase("ja, benutzer löschen")]
    [TestCase("yes, delete the user")]
    public void ResolvePendingConfirmation_AffirmationThatRestatesTheMutation_StillForcesConfirmation(string message)
    {
        SetPending();

        var (force, confirmFunction, note) = _service.ResolvePendingConfirmation(Context(message));

        Assert.That(MutationIntentDetector.IsMutationIntent(message), Is.True,
            "test would not cover the veto if the message carried no mutation intent");
        Assert.That(force, Is.True);
        Assert.That(confirmFunction!.Name, Is.EqualTo(AutonomyDefaults.ConfirmPendingActionSkillName));
        Assert.That(note, Does.Contain(PendingToken));
        Assert.That(note, Does.Contain(PendingSkillName));
    }

    [TestCase("ja")]
    [TestCase("ok")]
    [TestCase("yes")]
    public void ResolvePendingConfirmation_PlainAffirmation_ForcesConfirmation(string message)
    {
        SetPending();

        var (force, confirmFunction, note) = _service.ResolvePendingConfirmation(Context(message));

        Assert.That(force, Is.True);
        Assert.That(confirmFunction!.Name, Is.EqualTo(AutonomyDefaults.ConfirmPendingActionSkillName));
        Assert.That(note, Does.Contain(PendingToken));
    }

    [TestCase("nein, lass es")]
    [TestCase("nicht löschen")]
    [TestCase("was kostet das?")]
    public void ResolvePendingConfirmation_NoAffirmation_DoesNotForce(string message)
    {
        SetPending();

        var (force, confirmFunction, note) = _service.ResolvePendingConfirmation(Context(message));

        Assert.That(force, Is.False);
        Assert.That(confirmFunction, Is.Null);
        Assert.That(note, Is.Null);
    }

    [Test]
    public void ResolvePendingConfirmation_NoPendingRow_DoesNotForce()
    {
        _confirmationStore
            .PeekLatestForUser(Arg.Any<Guid>(), Arg.Any<TimeSpan>(), Arg.Any<string>())
            .Returns((PendingConfirmationHandle?)null);

        var (force, confirmFunction, note) = _service.ResolvePendingConfirmation(Context("ja"));

        Assert.That(force, Is.False);
        Assert.That(confirmFunction, Is.Null);
        Assert.That(note, Is.Null);
    }

    [Test]
    public void ResolvePendingConfirmation_ConfirmSkillOutOfScope_DoesNotForce()
    {
        SetPending();
        var context = Context("ja");
        context.AvailableFunctions = [new LLMFunction { Name = "some_other_skill" }];

        var (force, _, _) = _service.ResolvePendingConfirmation(context);

        Assert.That(force, Is.False);
    }

    [Test]
    public void ResolvePendingConfirmation_ReadsOnlyGateReplayRows()
    {
        SetPending();

        _service.ResolvePendingConfirmation(Context("ja"));

        _confirmationStore.Received(1).PeekLatestForUser(
            UserId,
            TimeSpan.FromSeconds(AutonomyDefaults.ConfirmationForceWindowSeconds),
            PendingConfirmationPurposes.GateReplay);
    }
}
