// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins what counts as a write in a replayed turn: the curated effect wins for offered tools, navigation and
/// catalogued read actions never count, and an unknown tool falls back to the read-only prefixes and is
/// otherwise treated as a write.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Enums;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnEvalWriteCallClassifierTests
{
    private const string MutatingTool = "place_work";
    private const string ExplainingTool = "explain_surcharge";
    private const string PrefixlessReadTool = "diagnose_holiday_outcome";
    private const string UnofferedWriteTool = "update_general_settings";
    private const string UnofferedReadTool = "search_employees";

    private static TurnReplayResult Replay(params string?[] stepTools)
    {
        var replay = new TurnReplayResult
        {
            Success = true,
            ChosenTool = stepTools.FirstOrDefault(),
            ToolEffects = new Dictionary<string, SkillEffect>(StringComparer.OrdinalIgnoreCase)
            {
                [MutatingTool] = SkillEffect.Mutate,
                [ExplainingTool] = SkillEffect.Explain,
                [PrefixlessReadTool] = SkillEffect.Read
            }
        };

        foreach (var tool in stepTools)
        {
            replay.Steps.Add(new TurnReplayStep { Tool = tool });
        }

        return replay;
    }

    [Test]
    public void OfferedMutatingTool_IsAWrite()
    {
        TurnEvalWriteCallClassifier.ReplayWroteAnything(Replay(MutatingTool)).ShouldBeTrue();
    }

    [Test]
    public void OfferedNonMutatingToolsWithoutReadPrefix_AreNoWrite()
    {
        TurnEvalWriteCallClassifier.ReplayWroteAnything(Replay(ExplainingTool)).ShouldBeFalse();
        TurnEvalWriteCallClassifier.ReplayWroteAnything(Replay(PrefixlessReadTool)).ShouldBeFalse();
    }

    [Test]
    public void Navigation_IsNoWrite()
    {
        TurnEvalWriteCallClassifier.ReplayWroteAnything(Replay("navigate_to")).ShouldBeFalse();
    }

    [Test]
    public void UnofferedTool_IsAWriteUnlessItCarriesAReadOnlyPrefix()
    {
        TurnEvalWriteCallClassifier.ReplayWroteAnything(Replay(UnofferedWriteTool)).ShouldBeTrue();
        TurnEvalWriteCallClassifier.ReplayWroteAnything(Replay(UnofferedReadTool)).ShouldBeFalse();
    }

    [Test]
    public void AWriteInTheSecondStepIsFound()
    {
        TurnEvalWriteCallClassifier.ReplayWroteAnything(Replay(UnofferedReadTool, MutatingTool)).ShouldBeTrue();
    }

    [Test]
    public void ReplayWithoutStepsFallsBackToTheChosenTool()
    {
        var replay = new TurnReplayResult { Success = true, ChosenTool = UnofferedWriteTool };

        TurnEvalWriteCallClassifier.ReplayWroteAnything(replay).ShouldBeTrue();
        TurnEvalWriteCallClassifier.ReplayCalledAnyTool(replay).ShouldBeTrue();
    }

    [Test]
    public void ReplayWithoutAnyToolCall_NeitherWroteNorCalled()
    {
        var replay = Replay((string?)null);

        TurnEvalWriteCallClassifier.ReplayWroteAnything(replay).ShouldBeFalse();
        TurnEvalWriteCallClassifier.ReplayCalledAnyTool(replay).ShouldBeFalse();
    }
}
