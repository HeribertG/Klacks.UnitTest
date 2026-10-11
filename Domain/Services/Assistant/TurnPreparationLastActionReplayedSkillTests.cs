// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// A confirmed held action is recorded with the wrapper as its SkillName and the replayed skill in a field of its own.
/// The undo path reads SkillName only, so recording the replayed skill must not make a confirmed write undoable.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class TurnPreparationLastActionReplayedSkillTests
{
    private const string ConversationKey = "conv-replayed";
    private const string ReplayedSkill = "update_contract";
    private const string ReversibleSkill = "add_shift_to_group";

    private static readonly Guid UserId = Guid.NewGuid();

    private AssistantLastAction? _saved;
    private TurnPreparationService _preparation = null!;
    private LLMContext _context = null!;

    [SetUp]
    public void SetUp()
    {
        _saved = null;
        var store = Substitute.For<IAssistantLastActionStore>();
        store.When(s => s.Save(Arg.Any<AssistantLastAction>()))
            .Do(call => _saved = call.Arg<AssistantLastAction>());

        _preparation = new TurnPreparationService(
            Substitute.For<IPendingConfirmationStore>(),
            null!,
            Substitute.For<IRecipeRunRecorder>(),
            null!,
            store,
            Substitute.For<IDeterministicRouteProbe>(),
            Substitute.For<ISkillInverseResolver>(),
            Substitute.For<ILogger<TurnPreparationService>>());

        _context = new LLMContext
        {
            Message = "Ja, bitte.",
            UserId = UserId.ToString(),
            Language = "de",
            AvailableFunctions = [new LLMFunction { Name = AutonomyDefaults.ConfirmPendingActionSkillName }]
        };
    }

    [Test]
    public void AConfirmedHeldAction_IsRecordedUnderTheWrapper_WithTheReplayedSkillAlongside()
    {
        var confirmed = new LLMFunctionCall
        {
            FunctionName = AutonomyDefaults.ConfirmPendingActionSkillName,
            Success = true,
            ReplayedSkillName = ReplayedSkill
        };

        _preparation.RecordLastAction(_context, ConversationKey, "Erledigt.", [confirmed], false);

        var call = _saved!.Calls.Single();
        call.SkillName.ShouldBe(AutonomyDefaults.ConfirmPendingActionSkillName);
        call.ReplayedSkillName.ShouldBe(ReplayedSkill);
        call.Success.ShouldBeTrue();
    }

    [Test]
    public void AnOrdinaryCall_IsRecordedWithoutAReplayedSkill()
    {
        var direct = new LLMFunctionCall { FunctionName = ReplayedSkill, Success = true };

        _preparation.RecordLastAction(_context, ConversationKey, "Erledigt.", [direct], false);

        _saved!.Calls.Single().ReplayedSkillName.ShouldBeNull();
    }

    [Test]
    public void AConfirmedReversibleWrite_StaysNotUndoable_ThroughItsLastActionRecord()
    {
        var inverseResolver = new SkillInverseResolver();
        var confirmed = new AssistantLastActionCall
        {
            SkillName = AutonomyDefaults.ConfirmPendingActionSkillName,
            ReplayedSkillName = ReversibleSkill,
            ArgumentsJson = """{"shiftId":"s-1","groupId":"g-1"}""",
            ResultDataJson = "{}",
            IsReadOnly = false,
            Success = true
        };
        var direct = new AssistantLastActionCall
        {
            SkillName = ReversibleSkill,
            ArgumentsJson = confirmed.ArgumentsJson,
            ResultDataJson = "{}",
            IsReadOnly = false,
            Success = true
        };

        inverseResolver.TryResolve(direct, out _).ShouldBeTrue();
        inverseResolver.TryResolve(confirmed, out var undo).ShouldBeFalse();
        undo.ShouldBeNull();
    }
}
