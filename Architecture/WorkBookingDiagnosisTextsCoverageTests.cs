// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Reflection;
using Klacks.Api.Application.Skills;

namespace Klacks.UnitTest.Architecture;

/// <summary>
/// Every refusal reason, remedy and outcome of the booking diagnosis has exactly one English sentence, so a new enum
/// value cannot reach the user as a raw name; no sentence names an enum value. "Sentence" means every text the class
/// holds: the reason, remedy and outcome tables and every public text constant (notes, channel sentences, the special
/// occupied-cell wordings, the relay instruction), found by reflection so a new note is covered without editing this
/// test.
/// </summary>
[TestFixture]
public class WorkBookingDiagnosisTextsCoverageTests
{
    private const char TokenStart = '{';

    [Test]
    public void EveryReason_HasASentence() =>
        Enum.GetValues<WorkBookingRefusalReason>().Where(value => !WorkBookingDiagnosisTexts.Reasons.ContainsKey(value))
            .ShouldBeEmpty();

    [Test]
    public void EveryRemedy_HasASentence() =>
        Enum.GetValues<WorkBookingRemedy>().Where(value => !WorkBookingDiagnosisTexts.Remedies.ContainsKey(value))
            .ShouldBeEmpty();

    [Test]
    public void EveryOutcome_HasASentence() =>
        Enum.GetValues<WorkBookingDiagnosisOutcome>().Where(value => !WorkBookingDiagnosisTexts.Outcomes.ContainsKey(value))
            .ShouldBeEmpty();

    [Test]
    public void TheSealKinds_AreReasonsOfTheirOwn_WithDifferentSentences() =>
        WorkBookingDiagnosisTexts.Reasons[WorkBookingRefusalReason.DayApproved]
            .ShouldNotBe(WorkBookingDiagnosisTexts.Reasons[WorkBookingRefusalReason.PeriodClosed]);

    [Test]
    public void TheReflectedSentences_IncludeTheNotesAndTheChannelSentences()
    {
        var sentences = AllSentences();

        sentences.ShouldContain(WorkBookingDiagnosisTexts.SporadicNotCheckedByAssistant);
        sentences.ShouldContain(WorkBookingDiagnosisTexts.ScenarioSkipsDirectCheck);
        sentences.ShouldContain(WorkBookingDiagnosisTexts.OccupiedByHiddenWork);
        sentences.ShouldContain(WorkBookingDiagnosisTexts.OccupiedByUnnamedWork);
        sentences.ShouldContain(WorkBookingDiagnosisTexts.DirectEntryRefuses);
        sentences.ShouldContain(WorkBookingDiagnosisTexts.RelayInstruction);
    }

    [Test]
    public void NoSentence_NamesAnEnumValue()
    {
        var sentences = AllSentences();
        var names = Enum.GetNames<WorkBookingRefusalReason>()
            .Concat(Enum.GetNames<WorkBookingRemedy>())
            .Concat(Enum.GetNames<WorkBookingDiagnosisOutcome>())
            .Concat(Enum.GetNames<WorkBookingSealKind>());

        foreach (var name in names)
        {
            sentences.Where(sentence => sentence.Contains(name, StringComparison.Ordinal)).ShouldBeEmpty(name);
        }
    }

    private static List<string> AllSentences()
    {
        var constants = typeof(WorkBookingDiagnosisTexts)
            .GetFields(BindingFlags.Public | BindingFlags.Static)
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .Where(text => !IsBareToken(text));

        return constants
            .Concat(WorkBookingDiagnosisTexts.Reasons.Values)
            .Concat(WorkBookingDiagnosisTexts.Remedies.Values)
            .Concat(WorkBookingDiagnosisTexts.Outcomes.Values)
            .ToList();
    }

    private static bool IsBareToken(string text) =>
        text.StartsWith(TokenStart) && text.IndexOf(' ', StringComparison.Ordinal) < 0;
}
