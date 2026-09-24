// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ClarificationTextService: in English (the default) every text has exactly the wording the
/// planner notices had before they were localized; the texts follow DEFAULT_LANGUAGE (core languages, a pack
/// language, a regional tag, the zh-CN casing) and an unknown language is English; the affected shift falls
/// back to the localized "none found" text; the unclear-answer notice is appended by line break; the status
/// word of a closed question and the neutral reply subject are localized too; the original text is shortened
/// and times are printed in one format; a value that looks like a placeholder is never expanded twice; and
/// an installed language whose pack lacks a key falls back to English with a warning instead of failing.
/// </summary>

using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Inbound;
using Klacks.Api.Infrastructure.Inbound;
using Klacks.Api.Infrastructure.Services.Settings;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Inbound;

[TestFixture]
public class ClarificationTextServiceTests
{
    private const string Question = "Are you sick today?";
    private const string Sender = "Anna Muster";
    private const string Summary = "Feels unwell.";
    private const string Shift = "Late shift 2026-09-23 14:00-22:00";
    private const string OriginalText = "I do not feel well.";
    private const string UnpackedLanguage = "xx-Test";
    private const string ChineseSimplified = "zh-CN";
    private static readonly DateTime Asked = new(2026, 9, 23, 9, 0, 0, DateTimeKind.Unspecified);
    private static readonly DateTime Deadline = new(2026, 9, 23, 10, 0, 0, DateTimeKind.Unspecified);

    private ISettingsReader _settingsReader = null!;
    private RecordingLogger<ClarificationTextService> _logger = null!;
    private ClarificationTextService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsReader = Substitute.For<ISettingsReader>();
        UseLanguage(null);
        _logger = new RecordingLogger<ClarificationTextService>();
        _service = new ClarificationTextService(
            new InstallationLanguageResolver(_settingsReader, Substitute.For<ILogger<InstallationLanguageResolver>>()), _logger);
    }

    [TearDown]
    public void ResetConfiguredTexts()
    {
        ClarificationTexts.Reset();
        GracefulCorrectionTexts.Reset();
    }

    private void UseLanguage(string? language) =>
        _settingsReader.GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(language == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { [SettingKeys.DefaultLanguage] = language });

    private static void LoadThePacks()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, "Klacks.Api", "Plugins", "Languages")))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull();
        var failures = new List<string>();
        AssistantTextsPluginLoader.Load(Path.Combine(directory.FullName, "Klacks.Api"), (file, ex) => failures.Add($"{file}: {ex.Message}"));
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    private static InboundClarification Closed(InboundClarificationStatus status, Guid? answerSourceId = null) => new()
    {
        Id = Guid.NewGuid(),
        Status = status,
        Question = Question,
        AnswerSourceId = answerSourceId
    };

    [Test]
    public async Task Started_InEnglish_HasTheWordingOfTheFormerHardcodedText()
    {
        var text = await _service.StartedAsync(Sender, Summary, Question, Shift, Deadline);

        text.ShouldBe(
            "💬 **Clarification requested** — Anna Muster\n" +
            "The message was unclear: Feels unwell.\n" +
            "Klacksy asked back privately: \"Are you sick today?\"\n" +
            "Affected shift: Late shift 2026-09-23 14:00-22:00\n" +
            "Answer expected by 2026-09-23 10:00. You will be informed about the answer, or when none arrives in time.");
    }

    [Test]
    public async Task Started_WithoutAShift_NamesNoShiftFoundInEnglish()
    {
        var text = await _service.StartedAsync(Sender, Summary, Question, null, Deadline);

        text.ShouldContain("Affected shift: none found in the plan\n");
    }

    [Test]
    public async Task Started_WithoutAShift_InGerman_NamesNoShiftFoundInGerman()
    {
        UseLanguage("de");

        var text = await _service.StartedAsync(Sender, Summary, Question, null, Deadline);

        text.ShouldContain("Betroffene Schicht: keine im Plan gefunden\n");
        text.ShouldNotContain("none found");
    }

    [Test]
    public async Task AnswerContext_InEnglish_HasTheWordingOfTheFormerHardcodedText()
    {
        var text = await _service.AnswerContextAsync(Question, Asked, OriginalText, unresolved: false);

        text.ShouldBe(
            "💬 Answer to Klacksy's question \"Are you sick today?\" (asked 2026-09-23 09:00).\n" +
            "Original message: I do not feel well.");
    }

    [Test]
    public async Task AnswerContext_Unresolved_AppendsTheUnclearNoticeOnANewLine()
    {
        var resolved = await _service.AnswerContextAsync(Question, Asked, OriginalText, unresolved: false);

        var text = await _service.AnswerContextAsync(Question, Asked, OriginalText, unresolved: true);

        text.ShouldBe(
            resolved + "\n⚠️ The answer is still unclear. Klacksy does not ask a second time, please follow up personally.");
    }

    [Test]
    public async Task AnswerContext_Unresolved_InGerman_AppendsTheGermanNotice()
    {
        UseLanguage("de");

        var text = await _service.AnswerContextAsync(Question, Asked, OriginalText, unresolved: true);

        text.ShouldContain("\n⚠️ Die Antwort ist weiterhin unklar.");
        text.ShouldContain("Ursprüngliche Nachricht: I do not feel well.");
    }

    [Test]
    public async Task AnswerContext_ShortensALongOriginalText()
    {
        var text = await _service.AnswerContextAsync(
            Question, Asked, new string('a', InboundClarificationConstants.MaxNotifiedOriginalTextLength + 50), unresolved: false);

        text.ShouldEndWith(new string('a', InboundClarificationConstants.MaxNotifiedOriginalTextLength) + "…");
    }

    [Test]
    public async Task Expired_InEnglish_HasTheWordingOfTheFormerHardcodedText()
    {
        var text = await _service.ExpiredAsync(Sender, Question, Asked, Deadline, OriginalText, Shift);

        text.ShouldBe(
            "⏰ **Question unanswered** — Anna Muster\n" +
            "Klacksy asked \"Are you sick today?\" at 2026-09-23 09:00; there was no answer by 2026-09-23 10:00.\n" +
            "Original message: I do not feel well.\n" +
            "Affected shift: Late shift 2026-09-23 14:00-22:00\n" +
            "Please follow up personally.");
    }

    [Test]
    public async Task TheShortNotices_InEnglish_HaveTheWordingOfTheFormerHardcodedTexts()
    {
        (await _service.SendFailedAsync(Question)).ShouldBe(
            "⚠️ Klacksy's question \"Are you sick today?\" could not be sent. Please follow up personally.");
        (await _service.NoPersonalTargetAsync()).ShouldBe(
            "ℹ️ The message is unclear, but Klacksy cannot ask back: no unambiguous personal contact could be determined " +
            "for this employee on this channel.");
        (await _service.AnsweredAfterExpiryAsync(Question, Asked)).ShouldBe(
            "ℹ️ This message arrived after Klacksy's question \"Are you sick today?\" (asked 2026-09-23 09:00) had expired unanswered.");
    }

    [Test]
    public async Task Suggested_InEnglish_NamesTheUiTermsOfTheAutonomyLevelAndTheKillSwitch()
    {
        var text = await _service.SuggestedAsync(Question);

        text.ShouldBe(
            "💡 Klacksy would ask back: \"Are you sick today?\" — questions are only sent automatically from the global " +
            "autonomy level Assisted upwards and as long as the kill switch (Master off switch for self-directed action) " +
            "has not been triggered.");
    }

    [TestCase(InboundClarificationStatus.Open, null, "waiting for the employee's answer")]
    [TestCase(InboundClarificationStatus.Answered, null, "answered by the employee")]
    [TestCase(InboundClarificationStatus.Expired, null, "not answered in time")]
    [TestCase(InboundClarificationStatus.TakenOver, null, "taken over by a planner")]
    [TestCase(InboundClarificationStatus.Suggested, null, "only suggested to the planners, not sent")]
    public async Task ArrivedAfterClosure_InEnglish_NamesTheStatusInWords(
        InboundClarificationStatus status, Guid? answerSourceId, string statusText)
    {
        var text = await _service.ArrivedAfterClosureAsync(Question, Asked, Closed(status, answerSourceId));

        text.ShouldBe(
            "ℹ️ This message arrived after Klacksy's question \"Are you sick today?\" (asked 2026-09-23 09:00) had already been closed: " +
            statusText + ".");
    }

    [Test]
    public async Task ArrivedAfterClosure_UnresolvedWithoutAnAnswer_NamesTheUndeliveredQuestion()
    {
        var text = await _service.ArrivedAfterClosureAsync(Question, Asked, Closed(InboundClarificationStatus.Unresolved));

        text.ShouldEndWith("closed: the question could not be delivered.");
    }

    [Test]
    public async Task ArrivedAfterClosure_UnresolvedWithAnAnswer_NamesTheUnclearAnswer()
    {
        var text = await _service.ArrivedAfterClosureAsync(Question, Asked, Closed(InboundClarificationStatus.Unresolved, Guid.NewGuid()));

        text.ShouldEndWith("closed: answered, but still unclear.");
    }

    [TestCase("de", "bereits geschlossen war: von einem Planer übernommen.")]
    [TestCase("fr", "(posée le 2026-09-23 09:00) : repris par un planificateur.")]
    [TestCase("it", "era già stata chiusa: preso in carico da un planner.")]
    public async Task ArrivedAfterClosure_NamesTheStatusInTheInstallationLanguage(string language, string ending)
    {
        UseLanguage(language);

        var text = await _service.ArrivedAfterClosureAsync(Question, Asked, Closed(InboundClarificationStatus.TakenOver));

        text.ShouldEndWith(ending);
        text.ShouldNotContain("taken over by a planner");
        text.ShouldNotContain("{");
    }

    [Test]
    public async Task NeutralReplySubject_FollowsTheInstallationLanguage()
    {
        (await _service.NeutralReplySubjectAsync()).ShouldBe("Re: Your message to the planning team");

        UseLanguage("de");
        (await _service.NeutralReplySubjectAsync()).ShouldBe("Re: Deine Nachricht an das Planungsteam");
    }

    [TestCase("de", "Klärung angefordert")]
    [TestCase("fr", "Clarification demandée")]
    [TestCase("it", "Chiarimento richiesto")]
    [TestCase("de-CH", "Klärung angefordert")]
    public async Task Started_FollowsTheCoreLanguageOfTheInstallation(string language, string heading)
    {
        UseLanguage(language);

        var text = await _service.StartedAsync(Sender, Summary, Question, Shift, Deadline);

        text.ShouldContain(heading);
        text.ShouldContain(Question);
        text.ShouldNotContain("Clarification requested");
    }

    [TestCase("zh-CN")]
    [TestCase("zh-cn")]
    [TestCase("zh-TW")]
    [TestCase("ja")]
    [TestCase("pt-BR")]
    public async Task Started_FollowsThePackLanguageOfTheInstallation_AndNeverFallsBackToEnglish(string language)
    {
        LoadThePacks();
        var english = await _service.StartedAsync(Sender, Summary, Question, Shift, Deadline);
        UseLanguage(language);

        var text = await _service.StartedAsync(Sender, Summary, Question, Shift, Deadline);

        text.ShouldNotBe(english);
        text.ShouldContain(Question);
        text.ShouldContain(Sender);
        text.ShouldNotContain("{");
        text.ShouldStartWith("💬 **");
    }

    [Test]
    public async Task Started_TheTwoChinesePacksDiffer()
    {
        LoadThePacks();
        UseLanguage(ChineseSimplified);
        var simplified = await _service.StartedAsync(Sender, Summary, Question, Shift, Deadline);
        UseLanguage("zh-TW");

        var traditional = await _service.StartedAsync(Sender, Summary, Question, Shift, Deadline);

        traditional.ShouldNotBe(simplified);
    }

    [Test]
    public async Task Started_AnUnknownLanguage_IsEnglish()
    {
        UseLanguage("xx-XX");

        var text = await _service.StartedAsync(Sender, Summary, Question, Shift, Deadline);

        text.ShouldContain("Clarification requested");
    }

    [Test]
    public async Task Started_AValueThatLooksLikeAPlaceholder_IsNotExpandedAgain()
    {
        var text = await _service.StartedAsync("{question}", "{sender} {shiftContext}", "{deadline}", "{summary}", Deadline);

        text.ShouldContain("💬 **Clarification requested** — {question}\n");
        text.ShouldContain("The message was unclear: {sender} {shiftContext}\n");
        text.ShouldContain("asked back privately: \"{deadline}\"\n");
        text.ShouldContain("Affected shift: {summary}\n");
    }

    [Test]
    public async Task AnInstalledLanguageWithAMissingKey_FallsBackToEnglishAndWarns()
    {
        UseLanguage(UnpackedLanguage);
        ClarificationTexts.Configure(UnpackedLanguage, new Dictionary<string, string> { ["some.other.key"] = "x" });

        var text = await _service.SendFailedAsync(Question);

        text.ShouldContain("could not be sent");
        _logger.Entries.ShouldContain(entry =>
            entry.Level == LogLevel.Warning
            && entry.Message.Contains(ClarificationTextKeys.PlannerSendFailed, StringComparison.Ordinal)
            && entry.Message.Contains(UnpackedLanguage, StringComparison.Ordinal));
    }

    [Test]
    public async Task AKnownLanguageThatIsInstalled_DoesNotWarn()
    {
        UseLanguage("de");

        await _service.StartedAsync(Sender, Summary, Question, null, Deadline);

        _logger.Entries.ShouldBeEmpty();
    }
}
