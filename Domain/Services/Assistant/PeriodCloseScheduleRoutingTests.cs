// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Routing tests for the read-only period-close-schedule recipe against the real recipe-seeds.json, ordered
/// by sortOrder like the engine: the questions about WHEN periods are closed reach the schedule dialog,
/// while every real close request still reaches close-payroll-period and status or audit questions reach
/// neither of the two.
/// </summary>

using System.Text.Json;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Infrastructure.Persistence.Seed.Models;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class PeriodCloseScheduleRoutingTests
{
    private const string RecipeSeedsFileName = "recipe-seeds.json";
    private const string ScheduleRecipe = "period-close-schedule";
    private const string CloseRecipe = "close-payroll-period";

    private static readonly string[] DefinitionsRelativePath =
    [
        "Klacks.Api", "Application", "Skills", "Definitions"
    ];

    private static readonly JsonSerializerOptions JsonReadOptions = new()
    {
        PropertyNameCaseInsensitive = true
    };

    private static List<RecipeSeedDefinition> _recipes = null!;

    [OneTimeSetUp]
    public void LoadSeededRecipes()
    {
        var file = LocateDefinitionsFile(RecipeSeedsFileName);
        var seed = JsonSerializer.Deserialize<RecipeSeedFile>(File.ReadAllText(file), JsonReadOptions);
        _recipes = seed!.Recipes.OrderBy(r => r.SortOrder).ToList();
    }

    private static string? Resolve(string message, string? language)
        => _recipes.FirstOrDefault(r => RecipeTriggerMatcher.Matches(r.Trigger, null, message, language))?.Name;

    [Test]
    [TestCase("Wann soll die Periode automatisch abgeschlossen werden?", "de")]
    [TestCase("Wann schliesst du Perioden automatisch ab?", "de")]
    [TestCase("Wann schließt du die Perioden automatisch ab?", "de")]
    [TestCase("Wie viele Tage nach Periodenende soll abgeschlossen werden?", "de")]
    [TestCase("When should periods be closed automatically?", "en")]
    [TestCase("How many days after the period ends should you close it?", "en")]
    [TestCase("Quand faut-il clôturer automatiquement la période ?", "fr")]
    [TestCase("Quando devi chiudere automaticamente il periodo?", "it")]
    public void QuestionsAboutTheCloseTiming_RouteToTheScheduleRecipe(string utterance, string language)
    {
        Assert.That(Resolve(utterance, language), Is.EqualTo(ScheduleRecipe));
    }

    [Test]
    [TestCase("Schliesse die Periode Januar ab", "de")]
    [TestCase("Bitte die Periode abschliessen", "de")]
    [TestCase("Close the period for January", "en")]
    [TestCase("Clôture le mois de janvier", "fr")]
    [TestCase("Chiudi il periodo di gennaio", "it")]
    public void RealCloseRequests_StillRouteToTheClosingRecipe(string utterance, string language)
    {
        Assert.That(Resolve(utterance, language), Is.EqualTo(CloseRecipe));
    }

    [Test]
    [TestCase("Wann wurde die Periode versiegelt?", "de")]
    [TestCase("When was the period sealed?", "en")]
    [TestCase("Erstelle einen Plan, wann die Periode abgeschlossen werden soll", "de")]
    [TestCase("Wie fange ich an?", "de")]
    public void StatusQuestionsAndCreateRequests_DoNotRouteToTheScheduleRecipe(string utterance, string language)
    {
        Assert.That(Resolve(utterance, language), Is.Not.EqualTo(ScheduleRecipe));
    }

    private static string LocateDefinitionsFile(string fileName)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine([directory.FullName, .. DefinitionsRelativePath, fileName]);
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new FileNotFoundException($"{fileName} not found above {AppContext.BaseDirectory}");
    }
}
