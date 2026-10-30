// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The question "what hangs on this employee's exit" belongs to evaluate_employee_exit and must not start the guided
/// recipe offboard-employee, whose trigger runs before function calling and leaves a pending confirmation behind. The
/// sentences are pinned against the real matcher: questions, conditional openers ("if she leaves ..., what happens"),
/// "can I record ..." and check/evaluate requests must be vetoed, while the requests that record the exit must keep
/// starting the recipe.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Models.Assistant.Recipes;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class EmployeeExitRecipeRoutingTests
{
    private const string OffboardRecipe = "offboard-employee";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions", "recipe-seeds.json"
    ];

    [TestCase("Wenn Anna Muster per Ende März austritt, was passiert mit ihren Einsätzen?", "de")]
    [TestCase("Was passiert, wenn Anna Muster zum Monatsende austritt?", "de")]
    [TestCase("Was hängt am Austritt von Anna Muster per 31.03.?", "de")]
    [TestCase("Kann ich den Austritt von Anna Muster per 31.03. erfassen?", "de")]
    [TestCase("Darf ich Anna Muster per Ende März austreten lassen?", "de")]
    [TestCase("Prüfe den Austritt von Anna Muster per Ende März", "de")]
    [TestCase("Prüf mal, was am Austritt von Anna Muster per Ende März hängt", "de")]
    [TestCase("Falls Anna Muster Ende März austritt, bleibt dann noch etwas gebucht?", "de")]
    [TestCase("Austritt von Anna Muster per Ende März: Was muss ich vorher klären?", "de")]
    [TestCase("Vor dem Austritt von Anna Muster per Ende März möchte ich wissen, was noch gebucht ist", "de")]
    [TestCase("Mit dem Austritt von Anna Muster per Ende März, bleiben da noch Pikett-Einsätze?", "de")]
    [TestCase("If Anna Muster leaves at the end of March, what happens to her shifts?", "en")]
    [TestCase("What hangs on the exit of Anna Muster on 31 March?", "en")]
    [TestCase("Can I record the exit of Anna Muster for 31 March?", "en")]
    [TestCase("Check the exit of Anna Muster at the end of March", "en")]
    [TestCase("Evaluate the exit of Anna Muster on 31 March", "en")]
    [TestCase("Before Anna Muster leaves at the end of March, I want to know what is still booked", "en")]
    [TestCase("Would absences remain after the termination date of Anna Muster?", "en")]
    [TestCase("Si Anna Muster part à la fin mars, que se passe-t-il pour ses services ?", "fr")]
    [TestCase("Puis-je enregistrer le départ d'Anna Muster au 31 mars ?", "fr")]
    [TestCase("Puis-je saisir la date de départ d'Anna Muster ou reste-t-il quelque chose d'ouvert ?", "fr")]
    [TestCase("Vérifie le départ d'Anna Muster fin mars", "fr")]
    [TestCase("Avant le départ d'Anna Muster fin mars, je voudrais savoir ce qui est encore planifié", "fr")]
    [TestCase("Se Anna Muster esce a fine marzo, cosa succede ai suoi turni?", "it")]
    [TestCase("Posso registrare l'uscita di Anna Muster al 31 marzo?", "it")]
    [TestCase("Verifica l'uscita di Anna Muster a fine marzo", "it")]
    [TestCase("Prima che Anna Muster esca a fine marzo vorrei sapere cosa è ancora pianificato", "it")]
    public void AdviceQuestion_DoesNotStartTheOffboardRecipe(string message, string language)
    {
        RecipeTriggerMatcher.Matches(OffboardTrigger(), null, message, language).ShouldBeFalse();
    }

    [TestCase("Erfasse den Austritt von Anna Muster per 31.03.", "de")]
    [TestCase("Kannst du den Austritt von Anna Muster erfassen", "de")]
    [TestCase("Trage den Austritt von Anna Muster per Ende März ein", "de")]
    [TestCase("Beende die Mitgliedschaft von Anna Muster per 31.03.", "de")]
    [TestCase("Setze das Anstellungsende von Anna Muster auf den 31.03.", "de")]
    [TestCase("Record the exit of Anna Muster on 31 March", "en")]
    [TestCase("Can you record the exit of Anna Muster", "en")]
    [TestCase("Record an employee's exit: set the end date of their active membership so the schedule stops planning them after the last working day.", "en")]
    [TestCase("Enregistre le départ d'Anna Muster au 31 mars", "fr")]
    [TestCase("Registra l'uscita di Anna Muster al 31 marzo", "it")]
    public void ExitRequest_KeepsStartingTheOffboardRecipe(string message, string language)
    {
        RecipeTriggerMatcher.Matches(OffboardTrigger(), null, message, language).ShouldBeTrue();
    }

    private static RecipeTrigger OffboardTrigger()
    {
        var path = RepositoryRootLocator.RequireFile(DefinitionsRelativePath);
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        var recipe = document.RootElement.GetProperty("recipes")
            .EnumerateArray()
            .Single(element => element.GetProperty("name").GetString() == OffboardRecipe);

        return recipe.GetProperty("trigger").Deserialize<RecipeTrigger>(
            new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
    }
}
