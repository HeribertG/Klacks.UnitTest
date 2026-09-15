// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for RecipeCorrectionDetector, the ask-step guard that stops a correction of the recipe
/// from being raw-filled into the pending slot.
///
/// The sharpest false-positive class is NOT a long unrelated sentence - it is a legitimate answer to the
/// very same entity-reference ask step that carries a negation ("Müller, nicht Meier"). Those are short,
/// so the character floor rejects them. The second sharpest is a long negation-bearing answer to a slot
/// that is NOT an entity reference ("nicht dasselbe wie letztes Jahr, alle zwei Wochen" into a criteria
/// or note slot); gate C1 rejects those, and it is the only thing that can: that message is 49 characters
/// and the English correction it must be told apart from is 50, so no length threshold separates them.
///
/// Positive cases are the four core languages plus one plugin-language phrase, because the detector owns
/// no vocabulary of its own and inherits its language coverage from ImplicitCorrectionDetector.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class RecipeCorrectionDetectorTests
{
    /// <summary>
    /// The live incident: raw-filled into clientName, then searched against 5000 clients.
    /// </summary>
    private const string LiveIncidentMessage =
        "Nein du hast mich missverstanden, alle Mitarbeitern, Externen und Kunden. Plural nicht singular";

    [TearDown]
    public void ResetPluginEntries()
    {
        ImplicitCorrectionDetector.Reset();
    }

    /// <summary>
    /// Mirrors add-extern-employee-to-nearest-group: the ask slot is injected into a search step that
    /// captures an id, so the answer must resolve to exactly one entity.
    /// </summary>
    private static RecipeExecutionPlan EntityReferenceAsk(bool captureRewindUsed = false) => new(
        "add-extern-employee-to-nearest-group",
        [
            new RecipeStep
            {
                Kind = RecipeStepKinds.Ask,
                Slot = "clientName",
                Prompt = "Ask for the name of the external employee",
                Description = "the name of the external employee"
            },
            new RecipeStep
            {
                Kind = RecipeStepKinds.Search,
                Skill = "search_employees",
                Inject = new Dictionary<string, string> { ["searchTerm"] = "$clientName" },
                Capture = "Array[].Id as clientId"
            },
            new RecipeStep
            {
                Kind = RecipeStepKinds.Mutate,
                Skill = "add_client_to_nearest_group",
                Inject = new Dictionary<string, string> { ["clientId"] = "$clientId" }
            }
        ],
        captureRewindUsed: captureRewindUsed);

    /// <summary>
    /// A free-text slot: long, negation-bearing answers are ordinary here, so the detector must stay out.
    /// </summary>
    private static RecipeExecutionPlan FreeTextAsk() => new(
        "create-shift-order",
        [
            new RecipeStep
            {
                Kind = RecipeStepKinds.Ask,
                Slot = "note",
                Prompt = "Ask for the note",
                Description = "a free text note"
            },
            new RecipeStep
            {
                Kind = RecipeStepKinds.Mutate,
                Skill = "create_shift_order",
                Inject = new Dictionary<string, string> { ["note"] = "$note" }
            }
        ]);

    /// <summary>
    /// A chip slot. "Nein, eigener Betrieb" is one of its offered replies, so a negation here is the
    /// expected answer and not a correction.
    /// </summary>
    private static RecipeExecutionPlan ChipAsk() => new(
        "setup-consultation",
        [
            new RecipeStep
            {
                Kind = RecipeStepKinds.Ask,
                Slot = "attribution",
                Prompt = "Ask whether the hours are attributed to a customer. " +
                         "Offer [REPLIES:single \"Ja, ein Kunde=yes\" | \"Nein, eigener Betrieb=no\"].",
                Description = "whether the hours are attributed to a customer"
            },
            new RecipeStep
            {
                Kind = RecipeStepKinds.Mutate,
                Skill = "update_settings",
                Inject = new Dictionary<string, string> { ["attribution"] = "$attribution" }
            }
        ]);

    [Test]
    public void IsStrongCorrection_True_ForTheLiveIncidentMessage()
    {
        RecipeCorrectionDetector.IsStrongCorrection(LiveIncidentMessage, EntityReferenceAsk())
            .ShouldBeTrue("the message that caused the incident must be recognized");
    }

    [TestCase("No, you misunderstood, all employees and customers")]
    [TestCase("Non, ce n'est pas ce que je voulais, tous les employés")]
    [TestCase("No, non intendevo questo, tutti i dipendenti e i clienti")]
    [TestCase("Nein, ich meinte nicht den einzelnen Mitarbeiter, sondern alle Kunden und Externen")]
    public void IsStrongCorrection_True_ForCoreLanguageCorrections(string message)
    {
        RecipeCorrectionDetector.IsStrongCorrection(message, EntityReferenceAsk()).ShouldBeTrue(message);
    }

    /// <summary>
    /// The detector has no vocabulary of its own, so its plugin coverage is inherited. "equivocado" is
    /// in none of the four core languages' token lists, so this only passes if the plugin entry reaches
    /// ImplicitCorrectionDetector - and it must not be a phrase containing a core token like "no es eso",
    /// which would pass anyway and prove nothing.
    /// </summary>
    [Test]
    public void IsStrongCorrection_True_ForAPluginLanguageCorrectionPhrase()
    {
        ImplicitCorrectionDetector.Configure(["equivocado"]);

        RecipeCorrectionDetector
            .IsStrongCorrection("Estás equivocado, quiero todos los empleados y clientes", EntityReferenceAsk())
            .ShouldBeTrue("a plugin corrections entry must reach the detector without a new list here");
    }

    /// <summary>
    /// The engine's own recovery path, and the reason the rewind flag is checked at all. An ambiguous
    /// capture rewinds the plan to this same ask slot and asks the user to be more specific; a
    /// disambiguation names two entities and therefore satisfies every gate. Aborting there would discard
    /// the slots already supplied and burn the one-shot rewind, turning the engine's two-attempt recovery
    /// into no attempts at all.
    /// </summary>
    [Test]
    public void IsStrongCorrection_False_WhileTheEngineIsDisambiguatingACapture()
    {
        const string disambiguation = "Nicht die Maria Meier aus Bern, ich meine die Maria Meier aus Zürich";

        RecipeCorrectionDetector
            .IsStrongCorrection(disambiguation, EntityReferenceAsk(captureRewindUsed: true))
            .ShouldBeFalse("the rewind is the engine asking for exactly this kind of answer");

        RecipeCorrectionDetector
            .IsStrongCorrection(disambiguation, EntityReferenceAsk())
            .ShouldBeTrue("control: without the rewind flag the same message reads as a correction, so the flag is what rejects it");
    }

    /// <summary>
    /// Sharpest false-positive class: legitimate answers to the SAME entity-reference ask step that
    /// happen to carry a negation. They are short, and that is the only thing distinguishing them.
    /// </summary>
    [TestCase("Müller, nicht Meier")]
    [TestCase("Nein")]
    [TestCase("nicht Max sondern Maria Muster")]
    [TestCase("Nein, Meier")]
    public void IsStrongCorrection_False_ForLegitimateEntityReferenceAnswers(string message)
    {
        RecipeCorrectionDetector.IsStrongCorrection(message, EntityReferenceAsk()).ShouldBeFalse(message);
    }

    /// <summary>
    /// Second sharpest class: long, negation-bearing, and a perfectly ordinary answer - but to a slot
    /// that is not an entity reference. Gate C1 is what rejects these, and it has to, because the first
    /// of them is 49 characters against the 50-character English correction above.
    /// Both cases must carry a correction cue, otherwise gate A rejects them first and the assertion
    /// proves nothing about C1.
    /// </summary>
    [TestCase("nicht dasselbe wie letztes Jahr, alle zwei Wochen")]
    [TestCase("nicht wie letztes Jahr, sondern alle zwei Wochen komplett neu planen")]
    public void IsStrongCorrection_False_ForLongNegationBearingFreeTextAnswers(string message)
    {
        RecipeCorrectionDetector.IsStrongCorrection(message, FreeTextAsk()).ShouldBeFalse(message);
    }

    [Test]
    public void IsStrongCorrection_False_ForTheLiveIncidentMessage_AtAFreeTextSlot()
    {
        RecipeCorrectionDetector.IsStrongCorrection(LiveIncidentMessage, FreeTextAsk())
            .ShouldBeFalse("without an entity-reference slot there is no state finding to lean on");
    }

    /// <summary>
    /// A chip reply that is literally one of the offered options must never read as a correction.
    /// </summary>
    [TestCase("Nein, eigener Betrieb")]
    [TestCase("Nein")]
    [TestCase("Weiss ich nicht")]
    public void IsStrongCorrection_False_ForChipReplies(string message)
    {
        RecipeCorrectionDetector.IsStrongCorrection(message, ChipAsk()).ShouldBeFalse(message);
    }

    /// <summary>
    /// Gate A is a necessary condition: a long message at an entity-reference slot without any
    /// contradiction is just an unusual name, not a correction.
    /// </summary>
    [Test]
    public void IsStrongCorrection_False_WithoutAContradictionCue()
    {
        RecipeCorrectionDetector
            .IsStrongCorrection("Die Mitarbeitenden der Spitex Bern aus dem Kanton Zürich", EntityReferenceAsk())
            .ShouldBeFalse("a cue is required; length and slot type alone must not abort a recipe");
    }

    [TestCase("Montag, aber nicht Dienstag")]
    public void IsStrongCorrection_False_ForTheSpecsNamedFalsePositive_AtACapturingSlot(string message)
    {
        RecipeCorrectionDetector.IsStrongCorrection(message, EntityReferenceAsk()).ShouldBeFalse(message);
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("   ")]
    public void IsStrongCorrection_False_ForEmptyOrWhitespace(string? message)
    {
        RecipeCorrectionDetector.IsStrongCorrection(message, EntityReferenceAsk())
            .ShouldBeFalse(message ?? "<null>");
    }

    [Test]
    public void IsStrongCorrection_False_WithoutAPendingPlan()
    {
        RecipeCorrectionDetector.IsStrongCorrection(LiveIncidentMessage, null).ShouldBeFalse();
    }
}
