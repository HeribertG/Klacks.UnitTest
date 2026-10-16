// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ScheduleReadIntentResolver: which turns must carry read_schedule_state. A dated duty or plan question in de/en/fr/it
/// guarantees read_schedule_state together with list_groups (it needs a group id); the schedule page alone guarantees
/// read_schedule_state only. Duty words match anywhere in a word (Nachtdienst, Bereitschaftsdienst); a weekday prefix is
/// not a duty word (Dienstagabend), and "hier" is not a date.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ScheduleReadIntentResolverTests
{
    private const string ScheduleRoute = "/workplace/schedule";
    private const string DashboardRoute = "/workplace/dashboard";
    private const string UnrelatedMessage = "Danke";

    private static readonly string[] ExpectedSkills =
    [
        ScheduleReadIntentResolver.ReadScheduleStateSkillName,
        ScheduleReadIntentResolver.ListGroupsSkillName
    ];

    [TestCase("Wer hat am Montag, 19. Oktober, in Winterthur Dienst?")]
    [TestCase("Was steht am Montag, 19. Oktober, in Winterthur an?")]
    [TestCase("Wer arbeitet morgen in Winterthur?")]
    [TestCase("Welche Schicht hat Anna am 19.10.?")]
    [TestCase("Zeig mir wer am Freitag eingeteilt ist")]
    [TestCase("Who is working on Monday, October 19 in Winterthur?")]
    [TestCase("Who has the shift tomorrow?")]
    [TestCase("What is scheduled for next week in Winterthur?")]
    [TestCase("Qui travaille lundi 19 octobre à Winterthur ?")]
    [TestCase("Quel service y a-t-il demain à Winterthur ?")]
    [TestCase("Chi lavora lunedì 19 ottobre a Winterthur?")]
    [TestCase("Quale turno c'è domani?")]
    [TestCase("Wie viele Dienste sind am Montag, 19. Oktober, in Winterthur geplant?")]
    [TestCase("Wer hat am Montag Nachtdienst?")]
    [TestCase("Wer macht am Freitag den Frühdienst?")]
    [TestCase("Wer hat am Samstag Bereitschaftsdienst?")]
    [TestCase("Wer hat Spätdienst am Mittwoch?")]
    [TestCase("Wer hat Pikett am Freitagabend?")]
    [TestCase("Wer hat montags Dienst?")]
    [TestCase("Wer hat Freitagabend Dienst?")]
    [TestCase("Wer hat Dienstagabend Dienst?")]
    [TestCase("Welcher Plan gilt am Montag?")]
    [TestCase("Wer hat am Montag den Montagsdienst?")]
    public void ADatedDutyQuestion_GuaranteesReadScheduleState(string message)
    {
        ScheduleReadIntentResolver.GuaranteedSkillNames(message, currentRoute: null)
            .ShouldBe(ExpectedSkills);
    }

    [TestCase("Erstelle am Montag, 19. Oktober, einen Dienst in Winterthur")]
    [TestCase("Was kostet ein Dienst?")]
    [TestCase("Wer ist der Chef?")]
    [TestCase("Wie ist das Wetter am Montag?")]
    [TestCase("Welche Mitarbeiter hat die Gruppe Winterthur?")]
    [TestCase("Was ist am Dienstag los?")]
    [TestCase("Create a shift on Monday in Winterthur")]
    [TestCase("Was ist am Dienstagabend los?")]
    [TestCase("Wer kommt am Dienstagabend?")]
    [TestCase("Wer hat hier Dienst?")]
    [TestCase("")]
    [TestCase("   ")]
    public void AnythingElse_GuaranteesNothing(string message)
    {
        ScheduleReadIntentResolver.GuaranteedSkillNames(message, currentRoute: null).ShouldBeEmpty();
    }

    [Test]
    public void ANullMessage_GuaranteesNothing()
    {
        ScheduleReadIntentResolver.GuaranteedSkillNames(null, currentRoute: null).ShouldBeEmpty();
    }

    [TestCase(ScheduleRoute)]
    [TestCase("/workplace/schedule/")]
    [TestCase("/workplace/schedule?group=1")]
    [TestCase("/workplace/schedule#today")]
    [TestCase("/WORKPLACE/Schedule")]
    public void TheSchedulePage_WithoutARecognisedQuestion_GuaranteesOnlyReadScheduleState(string route)
    {
        ScheduleReadIntentResolver.GuaranteedSkillNames("Kto pracuje w poniedziałek?", route)
            .ShouldBe([ScheduleReadIntentResolver.ReadScheduleStateSkillName]);
    }

    [Test]
    public void TheSchedulePage_WithARecognisedQuestion_GuaranteesBothSkills()
    {
        ScheduleReadIntentResolver.GuaranteedSkillNames("Wer hat am Montag Dienst?", ScheduleRoute)
            .ShouldBe(ExpectedSkills);
    }

    [TestCase(DashboardRoute)]
    [TestCase("/workplace/schedule-notes")]
    [TestCase("/workplace/client")]
    [TestCase(null)]
    [TestCase("")]
    public void AnyOtherPage_WithAnUnrelatedMessage_GuaranteesNothing(string? route)
    {
        ScheduleReadIntentResolver.GuaranteedSkillNames(UnrelatedMessage, route).ShouldBeEmpty();
    }
}
