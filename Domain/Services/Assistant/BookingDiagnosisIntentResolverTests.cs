// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Domain.Services.Assistant;

/// <summary>
/// BookingDiagnosisIntentResolver: a "why ... not" question in de/en/fr/it guarantees the booking diagnosis and the
/// general explanation when it names a day or is asked on the schedule page; without the negation, without "why", or
/// off the schedule page without a day nothing is guaranteed; "nichts"/"nothing" are no negation.
/// </summary>
[TestFixture]
public class BookingDiagnosisIntentResolverTests
{
    private const string ScheduleRoute = "/workplace/schedule";
    private const string DashboardRoute = "/workplace/dashboard";

    private static readonly string[] Expected =
    [
        SkillNames.DiagnoseWorkBooking,
        SkillNames.DiagnosePlanningPeriod,
        SkillNames.ExplainWhyCannotBook,
    ];

    [TestCase("Warum kann Anna am Montag den Frühdienst nicht bekommen?")]
    [TestCase("Wieso lässt sich für Peter am 12.10. kein Nachtdienst eintragen?")]
    [TestCase("Why can't Anna get the early shift on Monday?")]
    [TestCase("Why is the night shift not possible for Peter tomorrow?")]
    [TestCase("Pourquoi Anna ne peut-elle pas avoir le service du matin lundi ?")]
    [TestCase("Perché Anna non può fare il turno di notte venerdì?")]
    public void ADatedWhyNotQuestion_GuaranteesBothSkills(string message) =>
        BookingDiagnosisIntentResolver.GuaranteedSkillNames(message, DashboardRoute).ShouldBe(Expected);

    [Test]
    public void OnTheSchedulePage_AWhyNotQuestionWithoutADay_GuaranteesBothSkills() =>
        BookingDiagnosisIntentResolver.GuaranteedSkillNames("Warum geht der Frühdienst für Anna nicht?", ScheduleRoute)
            .ShouldBe(Expected);

    [TestCase("Warum geht der Frühdienst für Anna nicht?", DashboardRoute)]
    [TestCase("Wer hat am Montag Frühdienst?", ScheduleRoute)]
    [TestCase("Warum hat Anna am Montag Frühdienst?", ScheduleRoute)]
    [TestCase("Anna kann am Montag nicht arbeiten.", ScheduleRoute)]
    [TestCase("Warum ändert sich am Montag nichts?", ScheduleRoute)]
    [TestCase("Why is nothing planned on Monday?", ScheduleRoute)]
    [TestCase("", ScheduleRoute)]
    public void WithoutAWhyNotQuestionOnTheRightPageOrDay_NothingIsGuaranteed(string message, string route) =>
        BookingDiagnosisIntentResolver.GuaranteedSkillNames(message, route).ShouldBeEmpty();
}
