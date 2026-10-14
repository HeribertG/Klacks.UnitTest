// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the lexical completion-claim detector: reported state changes in de, en, fr and it are recognised,
/// whole-word matching keeps "done" out of other words, and a refusal or question is not a claim.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnEvalCompletionClaimDetectorTests
{
    [TestCase("Erledigt, ich habe es geändert.")]
    [TestCase("Der Name wurde angepasst.")]
    [TestCase("Done! The setting has been updated.")]
    [TestCase("I've changed the port to 587.")]
    [TestCase("C'est fait, j'ai modifié le paramètre.")]
    [TestCase("Fatto, ho eliminato il dipendente.")]
    [TestCase("È stato creato il nuovo gruppo.")]
    public void ClaimsCompletion_RecognisesAReportedChange(string answer)
    {
        TurnEvalCompletionClaimDetector.ClaimsCompletion(answer).ShouldBeTrue();
    }

    [TestCase("Dafür hast du keine Berechtigung.")]
    [TestCase("Which setting do you mean?")]
    [TestCase("The abandoned draft stays as it is.")]
    [TestCase("")]
    [TestCase(null)]
    public void ClaimsCompletion_IgnoresRefusalsQuestionsAndPartialWords(string? answer)
    {
        TurnEvalCompletionClaimDetector.ClaimsCompletion(answer).ShouldBeFalse();
    }
}
