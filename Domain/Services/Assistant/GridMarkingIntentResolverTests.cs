// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

/// <summary>
/// GridMarkingIntentResolver: a question about the colour of the schedule cells (dark, blue, "the colour of the cells") in
/// de/en/fr/it guarantees the schedule explanation and the planning-period diagnosis, but only on the schedule page; off
/// that page, or without a colour word, or with a colour word that is no shading ("Blumen") nothing is guaranteed.
/// </summary>
[TestFixture]
public class GridMarkingIntentResolverTests
{
    private const string ScheduleRoute = "/workplace/schedule";
    private const string DashboardRoute = "/workplace/dashboard";

    private static readonly string[] Expected = [SkillNames.ExplainPageSchedule, SkillNames.DiagnosePlanningPeriod];

    [TestCase("Warum sind die Zellen dunkelblau")]
    [TestCase("Warum sind die Zellen blau?")]
    [TestCase("Wieso ist das Feld dunkel?")]
    [TestCase("Was bedeutet die Farbe der Zellen?")]
    [TestCase("Weshalb sind einige Zellen so dunkel eingefärbt?")]
    [TestCase("Why are the cells dark blue?")]
    [TestCase("What does the colour of the cells mean?")]
    [TestCase("Why is this cell shaded dark?")]
    [TestCase("Pourquoi les cellules sont bleu foncé ?")]
    [TestCase("Que signifie la couleur des cellules ?")]
    [TestCase("Perché le celle sono blu scuro?")]
    [TestCase("Cosa significa il colore delle celle?")]
    [TestCase("Perché la cella è blu?")]
    public void AColourQuestionOnTheSchedulePage_GuaranteesTheExplanationAndTheDiagnosis(string message) =>
        GridMarkingIntentResolver.GuaranteedSkillNames(message, ScheduleRoute).ShouldBe(Expected);

    [Test]
    public void TheSchedulePageWithQueryAndEntityIds_CountsAsTheSchedulePage() =>
        GridMarkingIntentResolver.GuaranteedSkillNames("Warum ist alles blau?", ScheduleRoute + "?group=1#top")
            .ShouldBe(Expected);

    [TestCase("Warum sind die Zellen dunkelblau", DashboardRoute)]
    [TestCase("Warum sind die Zellen dunkelblau", null)]
    [TestCase("Warum sind die Zellen dunkelblau", "")]
    [TestCase("Wer hat am Montag Dienst?", ScheduleRoute)]
    [TestCase("Warum kann Anna am Montag den Frühdienst nicht bekommen?", ScheduleRoute)]
    [TestCase("Wo finde ich die Blumen für Anna?", ScheduleRoute)]
    [TestCase("Welche Farbe hat die Gruppe Bern?", ScheduleRoute)]
    [TestCase("", ScheduleRoute)]
    [TestCase(null, ScheduleRoute)]
    public void WithoutAShadingQuestionOnTheSchedulePage_NothingIsGuaranteed(string? message, string? route) =>
        GridMarkingIntentResolver.GuaranteedSkillNames(message, route).ShouldBeEmpty();
}
