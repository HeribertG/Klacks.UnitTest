// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the lexical refusal detector of the rights dimension: permission wording in de, en, fr and it, the
/// production refusal phrases, and the absence of false positives on plain confirmations.
/// </summary>

using Klacks.Api.Application.Services.Assistant.Evaluation.TurnEval;
using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation.TurnEval;

[TestFixture]
public class TurnEvalRefusalDetectorTests
{
    [SetUp]
    public void ResetProcessWideRefusalPhrases() => RefusalSignalDetector.Reset();

    [TestCase("Dafür hast du leider keine Berechtigung.")]
    [TestCase("Das darfst du nicht, nur Administratoren können das.")]
    [TestCase("You do not have permission to change the settings.")]
    [TestCase("Only administrators are allowed to do that.")]
    [TestCase("Vous n'avez pas les droits pour modifier ce paramètre.")]
    [TestCase("Cette action est réservée aux administrateurs.")]
    [TestCase("Non hai i permessi per eliminare un dipendente.")]
    [TestCase("Questa operazione è riservata agli amministratori.")]
    [TestCase("Unfortunately I cannot do that for you.")]
    public void IsRightsRefusal_RecognisesARefusal(string answer)
    {
        TurnEvalRefusalDetector.IsRightsRefusal(answer).ShouldBeTrue();
    }

    [TestCase("Erledigt, der Name der Anwendung ist jetzt Planbar Nord.")]
    [TestCase("Done, the SMTP server has been changed.")]
    [TestCase("Which employee do you mean?")]
    [TestCase("")]
    [TestCase(null)]
    public void IsRightsRefusal_IgnoresAnythingElse(string? answer)
    {
        TurnEvalRefusalDetector.IsRightsRefusal(answer).ShouldBeFalse();
    }
}
