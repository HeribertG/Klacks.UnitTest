// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Rule 3 end to end, with the real SkillInverseResolver and the real registry behind it: a correction
/// offers to undo the FIRST successful, lossless write of the corrected turn - never a read it did on the
/// way, never a call that failed, never a second offer - and it offers nothing at all when no inverse is
/// declared. Also pins that resolving an undo writes nothing: the pending confirmation is the entry
/// points' job, so a replay can score the same offer without touching a user's account.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class TurnPreparationCorrectionUndoTests
{
    private const string Correction = "Nein, ich meinte alle Mitarbeitenden in die Gruppe.";
    private const string PreviousMessage = "Nimm das in die Gruppe Zürich auf.";
    private const string UserId = "11111111-1111-1111-1111-111111111111";
    private const string ReadSkillName = "find_customer_candidates";
    private const string ReadSkillLabel = "Finds matching customers";
    private const string WriteSkillName = "add_shift_to_group";
    private const string WriteSkillLabel = "Assigns a shift to a group";
    private const string InverseSkillName = "remove_shift_from_group";
    private const string WriteArgumentsJson = """{"shiftId":"s-1","groupId":"g-1"}""";
    private const string UnmappedWriteSkillName = "update_client";

    private IPendingConfirmationStore _confirmationStore = null!;
    private TurnPreparationService _service = null!;

    [SetUp]
    public void SetUp()
    {
        var routeProbe = Substitute.For<IDeterministicRouteProbe>();
        routeProbe.GuaranteedSkillNamesAsync(
                Arg.Any<Agent?>(), Arg.Any<IReadOnlyList<string>>(), Arg.Any<string>(),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(new List<string>());

        _confirmationStore = Substitute.For<IPendingConfirmationStore>();

        _service = new TurnPreparationService(
            _confirmationStore,
            recipeEngine: null!,
            recipeRunRecorder: Substitute.For<IRecipeRunRecorder>(),
            slotExtractor: null!,
            lastActionStore: Substitute.For<IAssistantLastActionStore>(),
            routeProbe: routeProbe,
            inverseResolver: new SkillInverseResolver(),
            logger: Substitute.For<ILogger<TurnPreparationService>>());
    }

    [TearDown]
    public void ResetDetectors()
    {
        DeclineDetector.Reset();
        ImplicitCorrectionDetector.Reset();
    }

    private static AssistantLastActionCall Read() => new()
    {
        SkillName = ReadSkillName,
        SkillDisplayLabel = ReadSkillLabel,
        ArgumentsJson = """{"searchString":"Zürich"}""",
        IsReadOnly = true,
        Success = true
    };

    private static AssistantLastActionCall Write(
        string skillName = WriteSkillName, bool success = true, string argumentsJson = WriteArgumentsJson) => new()
    {
        SkillName = skillName,
        SkillDisplayLabel = WriteSkillLabel,
        ArgumentsJson = argumentsJson,
        IsReadOnly = false,
        Success = success
    };

    private static AssistantLastAction Anchor(params AssistantLastActionCall[] calls) => new()
    {
        UserId = Guid.Parse(UserId),
        ConversationId = "conv-1",
        UserMessage = PreviousMessage,
        AssistantAnswerExcerpt = "Der Dienst wurde der Gruppe zugeordnet.",
        CreateTimeUtc = DateTime.UtcNow,
        Calls = calls
    };

    private async Task<GracefulCorrectionOutcome> Complete(AssistantLastAction anchor)
    {
        var plan = await _service.PlanCorrectionAsync(
            new GracefulCorrectionInput(
                new Agent { Id = Guid.NewGuid(), Name = "Klacksy" }, new List<string>(), Correction,
                "conv-1", UserId, "de", anchor, RecipeIsActive: false),
            CancellationToken.None);

        plan.ShouldNotBeNull();

        return _service.CompleteCorrection(
            plan!,
            [new LLMFunction { Name = "fill_group_by_criteria", Description = "Fills a group.", ToolsetSource = ToolsetSkillSource.Keyword }],
            "de");
    }

    [Test]
    public async Task ASuccessfulLosslessWrite_IsOfferedForUndoWithItsCopiedArguments()
    {
        var outcome = await Complete(Anchor(Write()));

        outcome.Undo.ShouldNotBeNull();
        outcome.Undo!.SkillName.ShouldBe(InverseSkillName);
        outcome.Undo.Arguments["shiftId"].ShouldBe("s-1");
        outcome.Undo.Arguments["groupId"].ShouldBe("g-1");
        outcome.UndoneSkillLabel.ShouldBe(WriteSkillLabel);
    }

    // The reversible call is not necessarily the first one: a turn that looked something up before it
    // wrote must not have the search named as the thing about to be undone.
    [Test]
    public async Task AReadBeforeTheWrite_IsSkipped_AndTheWriteIsNamed()
    {
        var outcome = await Complete(Anchor(Read(), Write()));

        outcome.Undo.ShouldNotBeNull();
        outcome.Undo!.SkillName.ShouldBe(InverseSkillName);
        outcome.UndoneSkillLabel.ShouldBe(WriteSkillLabel);
        outcome.ContextNote.ShouldNotContain(ReadSkillName);
    }

    // A turn that wrote twice still asks once: rule 3 is a single yes/no sentence, not a menu.
    [Test]
    public async Task TwoReversibleWrites_ProduceExactlyOneOffer()
    {
        var outcome = await Complete(Anchor(Write(), Write()));

        outcome.Undo.ShouldNotBeNull();
        outcome.ContextNote.Split(InverseSkillName).Length.ShouldBe(2);
    }

    [Test]
    public async Task AFailedWrite_IsNotOffered_BecauseNothingChanged()
    {
        var outcome = await Complete(Anchor(Write(success: false)));

        outcome.Undo.ShouldBeNull();
        outcome.UndoneSkillLabel.ShouldBeNull();
    }

    [Test]
    public async Task AWriteWithoutADeclaredInverse_IsNotOffered()
    {
        var outcome = await Complete(Anchor(Write(skillName: UnmappedWriteSkillName)));

        outcome.Undo.ShouldBeNull();
    }

    [Test]
    public async Task AnIncompleteArgumentSet_IsNotOffered_RatherThanHalfFilled()
    {
        var outcome = await Complete(Anchor(Write(argumentsJson: """{"shiftId":"s-1"}""")));

        outcome.Undo.ShouldBeNull();
    }

    [Test]
    public async Task ResolvingAnUndo_WritesNoPendingConfirmation()
    {
        await Complete(Anchor(Write()));

        _confirmationStore.DidNotReceiveWithAnyArgs().Create(
            Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<IReadOnlyDictionary<string, object>>());
    }
}
