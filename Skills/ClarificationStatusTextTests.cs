// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Inbound;
using Klacks.Api.Domain.Services.Inbound;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ClarificationStatusTextTests
{
    private const string Question = "Which shift do you mean?";
    private static readonly Guid AnswerSource = Guid.NewGuid();
    private static readonly Guid ClarificationId = Guid.NewGuid();

    private static InboundClarification Build(
        InboundClarificationStatus status,
        Guid? answerSourceId = null,
        string question = Question) => new()
    {
        Id = ClarificationId,
        Status = status,
        Question = question,
        ShiftContext = "Early shift, Site A",
        AskedAt = new DateTime(2026, 9, 24, 8, 0, 0, DateTimeKind.Utc),
        DeadlineAt = new DateTime(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc),
        ResolvedAt = new DateTime(2026, 9, 24, 9, 30, 0, DateTimeKind.Utc),
        AnswerSourceId = answerSourceId
    };

    private static T Read<T>(object data, string property) =>
        (T)data.GetType().GetProperty(property)!.GetValue(data)!;

    [TestCase(InboundClarificationStatus.Open, "waiting for the employee's answer")]
    [TestCase(InboundClarificationStatus.Answered, "answered by the employee")]
    [TestCase(InboundClarificationStatus.Expired, "not answered in time")]
    [TestCase(InboundClarificationStatus.TakenOver, "taken over by a planner")]
    public void Sentence_AskedStatuses_SayTheEmployeeWasAskedBack(InboundClarificationStatus status, string expectedStatus)
    {
        var sentence = ClarificationStatusText.Sentence(Build(status));

        sentence.ShouldBe($" Klacksy asked the employee back: \"{Question}\" ({expectedStatus}).");
    }

    [Test]
    public void Sentence_UnresolvedWithAnswer_KeepsAnsweredButUnclear()
    {
        var sentence = ClarificationStatusText.Sentence(Build(InboundClarificationStatus.Unresolved, AnswerSource));

        sentence.ShouldBe($" Klacksy asked the employee back: \"{Question}\" (answered, but still unclear).");
    }

    [Test]
    public void Sentence_Suggested_SaysItWasNeverSent()
    {
        var sentence = ClarificationStatusText.Sentence(Build(InboundClarificationStatus.Suggested));

        sentence.ShouldBe($" Klacksy would ask the employee: \"{Question}\" (only suggested to the planners, not sent).");
        sentence.ShouldNotContain("asked the employee back");
    }

    [Test]
    public void Sentence_UnresolvedWithoutAnswer_SaysDeliveryFailed()
    {
        var sentence = ClarificationStatusText.Sentence(Build(InboundClarificationStatus.Unresolved));

        sentence.ShouldBe($" Klacksy tried to ask the employee: \"{Question}\" (the question could not be delivered).");
        sentence.ShouldNotContain("asked the employee back");
    }

    [Test]
    public void Sentence_UnknownStatus_SaysUnknown()
    {
        var sentence = ClarificationStatusText.Sentence(Build((InboundClarificationStatus)999));

        sentence.ShouldBe($" Klacksy asked the employee back: \"{Question}\" (unknown).");
    }

    [Test]
    public void Sentence_QuestionWithDoubleQuotes_ReplacesThemWithSingleQuotes()
    {
        var sentence = ClarificationStatusText.Sentence(
            Build(InboundClarificationStatus.Open, question: "Do you mean the \"late\" shift?"));

        sentence.ShouldContain("\"Do you mean the 'late' shift?\"");
        sentence.Count(c => c == '"').ShouldBe(2);
    }

    [TestCase(InboundClarificationStatus.Open, "waiting for the employee's answer")]
    [TestCase(InboundClarificationStatus.Answered, "answered by the employee")]
    [TestCase(InboundClarificationStatus.Expired, "not answered in time")]
    [TestCase(InboundClarificationStatus.TakenOver, "taken over by a planner")]
    [TestCase(InboundClarificationStatus.Suggested, "only suggested to the planners, not sent")]
    [TestCase(InboundClarificationStatus.Unresolved, "answered, but still unclear")]
    [TestCase((InboundClarificationStatus)999, "unknown")]
    public void Describe_ByStatus_ReturnsPlainLanguage(InboundClarificationStatus status, string expected)
    {
        ClarificationStatusText.Describe(status).ShouldBe(expected);
    }

    [Test]
    public void Describe_UnresolvedWithAnswer_IsAnsweredButUnclear()
    {
        ClarificationStatusText.Describe(Build(InboundClarificationStatus.Unresolved, AnswerSource))
            .ShouldBe("answered, but still unclear");
    }

    [Test]
    public void Describe_UnresolvedWithoutAnswer_IsNotDelivered()
    {
        ClarificationStatusText.Describe(Build(InboundClarificationStatus.Unresolved))
            .ShouldBe("the question could not be delivered");
    }

    [TestCase(InboundClarificationStatus.Open)]
    [TestCase(InboundClarificationStatus.Answered)]
    [TestCase(InboundClarificationStatus.Expired)]
    [TestCase(InboundClarificationStatus.TakenOver)]
    [TestCase(InboundClarificationStatus.Suggested)]
    public void Describe_ClarificationOverload_MatchesStatusOverloadForOtherStatuses(InboundClarificationStatus status)
    {
        ClarificationStatusText.Describe(Build(status)).ShouldBe(ClarificationStatusText.Describe(status));
    }

    [Test]
    public void ToSkillData_ExposesPlainStatusAndFields()
    {
        var clarification = Build(InboundClarificationStatus.Unresolved, AnswerSource);

        var data = ClarificationStatusText.ToSkillData(clarification);

        Read<Guid>(data, "Id").ShouldBe(ClarificationId);
        Read<string>(data, "Status").ShouldBe("answered, but still unclear");
        Read<string>(data, "Question").ShouldBe(Question);
        Read<string>(data, "ShiftContext").ShouldBe("Early shift, Site A");
        Read<DateTime>(data, "AskedAt").ShouldBe(clarification.AskedAt);
        Read<DateTime>(data, "DeadlineAt").ShouldBe(clarification.DeadlineAt);
        Read<DateTime?>(data, "ResolvedAt").ShouldBe(clarification.ResolvedAt);
    }

    [Test]
    public void ToSkillData_UnresolvedWithoutAnswer_ReportsDeliveryFailure()
    {
        var data = ClarificationStatusText.ToSkillData(Build(InboundClarificationStatus.Unresolved));

        Read<string>(data, "Status").ShouldBe("the question could not be delivered");
    }

    [Test]
    public void ToSkillData_Suggested_ReportsNotSent()
    {
        var data = ClarificationStatusText.ToSkillData(Build(InboundClarificationStatus.Suggested));

        Read<string>(data, "Status").ShouldBe("only suggested to the planners, not sent");
    }

    [Test]
    public void Texts_NeverContainEnumNames()
    {
        foreach (var status in Enum.GetValues<InboundClarificationStatus>())
        {
            var clarification = Build(status);
            var text = ClarificationStatusText.Sentence(clarification) + ClarificationStatusText.Describe(clarification);

            foreach (var name in Enum.GetNames<InboundClarificationStatus>())
            {
                text.ShouldNotContain(name, Case.Sensitive);
            }
        }
    }

    [TestCase(InboundClarificationStatus.Open, false, ClarificationTextKeys.StatusOpen)]
    [TestCase(InboundClarificationStatus.Answered, false, ClarificationTextKeys.StatusAnswered)]
    [TestCase(InboundClarificationStatus.Unresolved, true, ClarificationTextKeys.StatusUnresolved)]
    [TestCase(InboundClarificationStatus.Unresolved, false, ClarificationTextKeys.StatusUndelivered)]
    [TestCase(InboundClarificationStatus.Expired, false, ClarificationTextKeys.StatusExpired)]
    [TestCase(InboundClarificationStatus.TakenOver, false, ClarificationTextKeys.StatusTakenOver)]
    [TestCase(InboundClarificationStatus.Suggested, false, ClarificationTextKeys.StatusSuggested)]
    public void KeyOf_MapsEveryStatusToItsCatalogueKey_AndTheEnglishWordsAreTheCatalogueEntries(
        InboundClarificationStatus status, bool hasAnswer, string expectedKey)
    {
        var clarification = Build(status, hasAnswer ? AnswerSource : null);

        ClarificationStatusText.KeyOf(clarification).ShouldBe(expectedKey);
        ClarificationStatusText.Describe(clarification).ShouldBe(ClarificationTexts.English(expectedKey));
    }

    [Test]
    public void KeyOf_AnUnknownStatus_MapsToTheUnknownKey()
    {
        ClarificationStatusText.KeyOf((InboundClarificationStatus)(-1)).ShouldBe(ClarificationTextKeys.StatusUnknown);
        ClarificationStatusText.Describe((InboundClarificationStatus)(-1)).ShouldBe("unknown");
    }
}
