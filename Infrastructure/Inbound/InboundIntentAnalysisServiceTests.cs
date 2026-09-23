// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for InboundIntentAnalysisService — verifies customer-fixed intent, LLM JSON parsing
/// (clean, embedded and broken replies), the system/user prompt split, the company-local Date line,
/// and that an LLM failure (provider error or exception) degrades to a recorded failure instead of an
/// exception. Client resolution and the enabled/disabled feature gate are the caller's responsibility
/// and are exercised in EmailPollingBackgroundServiceTests / MessengerIntentProcessorTests instead.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Inbound;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Inbound;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Inbound;

[TestFixture]
public class InboundIntentAnalysisServiceTests
{
    private static readonly ScheduleCommandKeywordSet DefaultKeywords = ScheduleCommandKeywordTestFactory.Default;

    private IOneShotCompletionService _completionService = null!;
    private IScheduleCommandKeywordProvider _keywordProvider = null!;
    private FixedCompanyClock _companyClock = null!;
    private InboundIntentAnalysisService _service = null!;

    private static readonly Guid ClientId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _completionService = Substitute.For<IOneShotCompletionService>();
        _keywordProvider = Substitute.For<IScheduleCommandKeywordProvider>();
        _keywordProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(DefaultKeywords);
        _companyClock = new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc);

        _service = new InboundIntentAnalysisService(
            _completionService, _keywordProvider, _companyClock,
            Substitute.For<ILogger<InboundIntentAnalysisService>>());
    }

    private void LlmReplies(string message) => CompletionReturns(OneShotCompletionResult.Succeeded(message));

    private void CompletionReturns(OneShotCompletionResult result) =>
        _completionService.CompleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(result);

    private void CompletionThrows(Exception exception) =>
        _completionService.CompleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<OneShotCompletionResult>(_ => throw exception);

    private static TimeZoneInfo FixedOffsetZone(int offsetHours)
    {
        var id = $"Test{offsetHours:+0;-0;0}";
        return TimeZoneInfo.CreateCustomTimeZone(id, TimeSpan.FromHours(offsetHours), id, id);
    }

    private static InboundSource Source() => new(
        Guid.NewGuid(), InboundSourceKind.Email, "Email", "worker@example.com", "Krankmeldung",
        "Ich bin krank und kann morgen nicht arbeiten.", new DateTime(2026, 7, 8, 8, 0, 0, DateTimeKind.Utc));

    [Test]
    public async Task EmployeeWorkCancellation_ParsesIntentSummaryAndDates()
    {
        LlmReplies("""{"intent":"WorkCancellation","summary":"Mitarbeiter meldet sich krank.","fromDate":"2026-07-09","untilDate":"2026-07-10"}""");

        var source = Source();
        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, source);

        result.ShouldNotBeNull();
        result.SourceKind.ShouldBe(InboundSourceKind.Email);
        result.SourceId.ShouldBe(source.SourceId);
        result.Channel.ShouldBe("Email");
        result.ClientId.ShouldBe(ClientId);
        result.ClientType.ShouldBe(EntityTypeEnum.Employee);
        result.Intent.ShouldBe(EmailIntent.WorkCancellation);
        result.Summary.ShouldBe("Mitarbeiter meldet sich krank.");
        result.FromDate.ShouldBe(new DateOnly(2026, 7, 9));
        result.UntilDate.ShouldBe(new DateOnly(2026, 7, 10));
        result.FailureReason.ShouldBeNull();
    }

    [Test]
    public async Task JsonEmbeddedInProse_IsStillParsed()
    {
        LlmReplies("""Here is the analysis: {"intent":"VacationRequest","summary":"Ferien im August.","fromDate":"2026-08-03","untilDate":"2026-08-14"} Done.""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.ExternEmp, Source());

        result.Intent.ShouldBe(EmailIntent.VacationRequest);
        result.FromDate.ShouldBe(new DateOnly(2026, 8, 3));
    }

    [Test]
    public async Task NullDates_MapToNull()
    {
        LlmReplies("""{"intent":"DayOffWish","summary":"Wunsch nach freien Tagen.","fromDate":null,"untilDate":null}""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.Intent.ShouldBe(EmailIntent.DayOffWish);
        result.FromDate.ShouldBeNull();
        result.UntilDate.ShouldBeNull();
    }

    [Test]
    public async Task Customer_AlwaysCustomerMessage_EvenIfLlmSaysOtherwise()
    {
        LlmReplies("""{"intent":"WorkCancellation","summary":"Kunde schreibt etwas.","fromDate":null,"untilDate":null}""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Customer, Source());

        result.Intent.ShouldBe(EmailIntent.CustomerMessage);
        result.Summary.ShouldBe("Kunde schreibt etwas.");
    }

    [Test]
    public async Task UnparsableReply_DegradesToOther_WithFailureReason()
    {
        LlmReplies("Sorry, I cannot help with that.");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.Intent.ShouldBe(EmailIntent.Other);
        result.FailureReason.ShouldNotBeNull();
    }

    [Test]
    public async Task LlmThrows_DegradesToFailureAnalysis_NoException()
    {
        CompletionThrows(new InvalidOperationException("provider down"));

        var source = Source();
        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, source);

        result.Intent.ShouldBe(EmailIntent.Other);
        result.Summary.ShouldBe(source.Subject);
        result.FailureReason.ShouldBe("provider down");
    }

    [Test]
    public async Task UnknownIntentString_MapsToOther()
    {
        LlmReplies("""{"intent":"SomethingNew","summary":"Unklar.","fromDate":null,"untilDate":null}""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.Intent.ShouldBe(EmailIntent.Other);
        result.FailureReason.ShouldBeNull();
    }

    [Test]
    public async Task AvailabilityAnnouncement_MapsIntentHourWindowAndWeekdays()
    {
        LlmReplies("""{"intent":"AvailabilityAnnouncement","summary":"Verfügbar im August.","fromDate":"2026-08-03","untilDate":"2026-08-28","startHour":8,"endHour":16,"weekdays":"2,1"}""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.Intent.ShouldBe(EmailIntent.AvailabilityAnnouncement);
        result.FromDate.ShouldBe(new DateOnly(2026, 8, 3));
        result.UntilDate.ShouldBe(new DateOnly(2026, 8, 28));
        result.StartHour.ShouldBe(8);
        result.EndHour.ShouldBe(16);
        result.Weekdays.ShouldBe("1,2");
    }

    [TestCase(25, 10)]
    [TestCase(16, 8)]
    public async Task InvalidHourWindow_MapsBothHoursToNull(int startHour, int endHour)
    {
        LlmReplies($$"""{"intent":"AvailabilityAnnouncement","summary":"Verfügbar.","fromDate":"2026-08-03","untilDate":"2026-08-07","startHour":{{startHour}},"endHour":{{endHour}},"weekdays":null}""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.StartHour.ShouldBeNull();
        result.EndHour.ShouldBeNull();
    }

    [Test]
    public async Task Weekdays_AreDeduplicatedSortedAndInvalidTokensDropped()
    {
        LlmReplies("""{"intent":"AvailabilityAnnouncement","summary":"Verfügbar.","fromDate":"2026-08-03","untilDate":"2026-08-28","startHour":null,"endHour":null,"weekdays":" 5, 1, 1, 9, x "}""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.Weekdays.ShouldBe("1,5");
    }

    [Test]
    public async Task HoursAsJsonStrings_AreParsed()
    {
        LlmReplies("""{"intent":"AvailabilityAnnouncement","summary":"Verfügbar.","fromDate":"2026-08-03","untilDate":"2026-08-07","startHour":"8","endHour":"16","weekdays":null}""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.StartHour.ShouldBe(8);
        result.EndHour.ShouldBe(16);
    }

    [Test]
    public async Task ShiftPreference_MapsIntentAndNormalizesScheduleCommands()
    {
        LlmReplies("""{"intent":"ShiftPreference","summary":"Kann nur früh arbeiten.","fromDate":"2026-08-03","untilDate":"2026-08-07","startHour":null,"endHour":null,"weekdays":null,"scheduleCommands":" early, -night, early, FOO "}""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.Intent.ShouldBe(EmailIntent.ShiftPreference);
        result.ScheduleCommands.ShouldBe("EARLY,-NIGHT");
    }

    [Test]
    public async Task ScheduleCommandsWithoutValidKeywords_MapToNull()
    {
        LlmReplies("""{"intent":"ShiftPreference","summary":"Unklare Präferenz.","fromDate":"2026-08-03","untilDate":"2026-08-07","startHour":null,"endHour":null,"weekdays":null,"scheduleCommands":"MORNING, FOO, "}""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.Intent.ShouldBe(EmailIntent.ShiftPreference);
        result.ScheduleCommands.ShouldBeNull();
    }

    [Test]
    public async Task ConfiguredKeyword_IsAcceptedAndPreservedInScheduleCommands()
    {
        _keywordProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(DefaultKeywords with { EarlyToken = "FRUEHDIENST" });
        LlmReplies("""{"intent":"ShiftPreference","summary":"Kann nur früh arbeiten.","fromDate":"2026-08-03","untilDate":"2026-08-07","scheduleCommands":"fruehdienst"}""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.ScheduleCommands.ShouldBe("FRUEHDIENST");
    }

    [Test]
    public async Task EnglishDefaultKeyword_IsDropped_WhenKeywordWasRenamed()
    {
        _keywordProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(DefaultKeywords with { EarlyToken = "FRUEHDIENST" });
        LlmReplies("""{"intent":"ShiftPreference","summary":"Kann nur früh arbeiten.","fromDate":"2026-08-03","untilDate":"2026-08-07","scheduleCommands":"EARLY"}""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.ScheduleCommands.ShouldBeNull();
    }

    [Test]
    public void Prompt_EmbedsCurrentlyConfiguredKeywordTokens()
    {
        var keywords = DefaultKeywords with { FreeToken = "URLAUB", NegNightToken = "KEINE_NACHT" };

        var prompt = InboundIntentAnalysisService.BuildPrompt(
            Source(), EntityTypeEnum.Employee, "body", new DateOnly(2026, 7, 8), keywords);

        prompt.SystemPrompt.ShouldContain("URLAUB");
        prompt.SystemPrompt.ShouldContain("KEINE_NACHT");
    }

    [TestCase("high", EmailConfidence.High)]
    [TestCase("low", EmailConfidence.Low)]
    [TestCase("HIGH", EmailConfidence.High)]
    public async Task Confidence_IsMappedFromLlmReply(string confidence, EmailConfidence expected)
    {
        LlmReplies($$"""{"intent":"AvailabilityAnnouncement","confidence":"{{confidence}}","summary":"Verfügbar.","fromDate":"2026-08-03","untilDate":"2026-08-07"}""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.Confidence.ShouldBe(expected);
    }

    [Test]
    public async Task MissingConfidence_MapsToUnknown_NeverSilentlyHigh()
    {
        LlmReplies("""{"intent":"AvailabilityAnnouncement","summary":"Verfügbar.","fromDate":"2026-08-03","untilDate":"2026-08-07"}""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.Confidence.ShouldBe(EmailConfidence.Unknown);
    }

    [Test]
    public async Task Customer_AlwaysHighConfidence_EvenIfLlmSaysLow()
    {
        LlmReplies("""{"intent":"CustomerMessage","confidence":"low","summary":"Kunde schreibt etwas.","fromDate":null,"untilDate":null}""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Customer, Source());

        result.Confidence.ShouldBe(EmailConfidence.High);
    }

    [Test]
    public async Task UnparsableReply_DegradesToLowConfidence()
    {
        LlmReplies("Sorry, I cannot help with that.");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.Confidence.ShouldBe(EmailConfidence.Low);
    }

    [Test]
    public async Task LlmThrows_DegradesToLowConfidence()
    {
        CompletionThrows(new InvalidOperationException("provider down"));

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.Confidence.ShouldBe(EmailConfidence.Low);
    }

    [Test]
    public void Prompt_PutsInstructionsIntoSystemPrompt_AndOnlyTheMessageDataIntoTheUserMessage()
    {
        var source = Source();

        var prompt = InboundIntentAnalysisService.BuildPrompt(
            source, EntityTypeEnum.Employee, source.Body, new DateOnly(2026, 7, 8), DefaultKeywords);

        prompt.SystemPrompt.ShouldContain("exactly one JSON object");
        prompt.SystemPrompt.ShouldContain("\"intent\"");
        prompt.SystemPrompt.ShouldContain("Rules:");
        prompt.SystemPrompt.ShouldNotContain(source.Body);
        prompt.SystemPrompt.ShouldNotContain("add any text");
        prompt.UserMessage.ShouldBe(
            $"From: {source.SenderDisplay}\nDate: 2026-07-08 (Wednesday)\nSubject: {source.Subject}\nBody: {source.Body}");
    }

    [Test]
    public void Prompt_WithoutSubject_OmitsTheSubjectLine()
    {
        var source = Source() with { Subject = null };

        var prompt = InboundIntentAnalysisService.BuildPrompt(
            source, EntityTypeEnum.Employee, source.Body, new DateOnly(2026, 7, 8), DefaultKeywords);

        prompt.UserMessage.ShouldNotContain("Subject:");
    }

    [Test]
    public void Prompt_InstructsToResolveRelativeDatesAgainstTheDateLine()
    {
        var prompt = InboundIntentAnalysisService.BuildPrompt(
            Source(), EntityTypeEnum.Employee, "body", new DateOnly(2026, 7, 8), DefaultKeywords);

        prompt.SystemPrompt.ShouldContain("Resolve relative date expressions");
        prompt.SystemPrompt.ShouldContain("local time zone");
    }

    [Test]
    public async Task Analyze_SendsSystemPromptAndUserMessageToTheOneShotCompletion()
    {
        string? capturedSystemPrompt = null;
        string? capturedUserMessage = null;
        _completionService.CompleteAsync(
                Arg.Do<string>(s => capturedSystemPrompt = s), Arg.Do<string>(u => capturedUserMessage = u),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(OneShotCompletionResult.Succeeded("""{"intent":"Other","confidence":"low","summary":"x"}"""));

        await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        capturedSystemPrompt.ShouldNotBeNull();
        capturedSystemPrompt!.ShouldContain("exactly one JSON object");
        capturedUserMessage.ShouldNotBeNull();
        capturedUserMessage!.ShouldContain("Body: Ich bin krank und kann morgen nicht arbeiten.");
        await _completionService.Received(1).CompleteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Is<string?>(modelId => modelId == null), Arg.Any<CancellationToken>());
    }

    [TestCase(2, 2026, 9, 22, 22, 30, "2026-09-23 (Wednesday)")]
    [TestCase(-5, 2026, 9, 23, 3, 30, "2026-09-22 (Tuesday)")]
    [TestCase(0, 2026, 9, 22, 22, 30, "2026-09-22 (Tuesday)")]
    public async Task DateLine_UsesTheCompanyLocalDay_NotTheUtcDay(
        int offsetHours, int year, int month, int day, int hour, int minute, string expectedDateLine)
    {
        _companyClock.TimeZone = FixedOffsetZone(offsetHours);
        string? capturedUserMessage = null;
        _completionService.CompleteAsync(
                Arg.Any<string>(), Arg.Do<string>(u => capturedUserMessage = u),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(OneShotCompletionResult.Succeeded("""{"intent":"Other","confidence":"low","summary":"x"}"""));
        var source = Source() with { ReceivedAt = new DateTime(year, month, day, hour, minute, 0, DateTimeKind.Utc) };

        await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, source);

        capturedUserMessage.ShouldNotBeNull();
        capturedUserMessage!.ShouldContain($"Date: {expectedDateLine}\n");
    }

    [Test]
    public void ToCompanyLocalDate_TreatsUnspecifiedKindAsUtc()
    {
        var unspecified = new DateTime(2026, 9, 22, 22, 30, 0, DateTimeKind.Unspecified);

        var localDate = InboundIntentAnalysisService.ToCompanyLocalDate(unspecified, FixedOffsetZone(2));

        localDate.ShouldBe(new DateOnly(2026, 9, 23));
    }

    [Test]
    public async Task ProviderFailure_RecordsLlmCallFailedReason_WithoutRetrying()
    {
        CompletionReturns(OneShotCompletionResult.Failed("Invalid API key"));
        var source = Source();

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, source);

        result.Intent.ShouldBe(EmailIntent.Other);
        result.Confidence.ShouldBe(EmailConfidence.Low);
        result.Summary.ShouldBe(source.Subject);
        result.FailureReason.ShouldBe("LLM call failed: Invalid API key");
        await _completionService.Received(1).CompleteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ProviderFailure_ForCustomer_StaysCustomerMessageWithHighConfidence()
    {
        CompletionReturns(OneShotCompletionResult.Failed("503 Service Unavailable"));

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Customer, Source());

        result.Intent.ShouldBe(EmailIntent.CustomerMessage);
        result.Confidence.ShouldBe(EmailConfidence.High);
        result.FailureReason.ShouldBe("LLM call failed: 503 Service Unavailable");
    }

    [Test]
    public async Task UnparsableReply_IsRetriedOnce_ThenRecordedAsNotParsable()
    {
        LlmReplies("Sorry, I cannot help with that.");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.FailureReason.ShouldBe("LLM reply was not parsable JSON after 2 attempts");
        await _completionService.Received(2).CompleteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public void Constructor_DoesNotTakeILLMService_BecauseTheChatPipelineRanRecipesAndMemoryOnForeignText()
    {
        var parameterTypes = typeof(InboundIntentAnalysisService).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        parameterTypes.ShouldNotContain(typeof(ILLMService));
        parameterTypes.ShouldContain(typeof(IOneShotCompletionService));
    }

    private static ClarificationHistory History() => new(
        "Ich fühle mich nicht gut.",
        new DateTime(2026, 7, 8, 5, 30, 0, DateTimeKind.Utc),
        "Heißt das, du kannst heute deinen Spätdienst (14:00-22:00) nicht antreten?",
        new DateTime(2026, 7, 8, 5, 31, 0, DateTimeKind.Utc));

    private static InboundSource AnswerSource() => new(
        Guid.NewGuid(), InboundSourceKind.Messenger, "Messenger:Telegram", "Anna Muster", null,
        "Ja, leider.", new DateTime(2026, 7, 8, 5, 40, 0, DateTimeKind.Utc));

    [Test]
    public async Task NeedsClarificationTrue_SetsFlagAndQuestion()
    {
        LlmReplies("""{"intent":"Other","confidence":"low","summary":"Fühlt sich nicht gut.","fromDate":null,"untilDate":null,"needsClarification":true,"clarificationQuestion":"Kannst du heute arbeiten?"}""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.NeedsClarification.ShouldBeTrue();
        result.ClarificationQuestion.ShouldBe("Kannst du heute arbeiten?");
    }

    [Test]
    public async Task NeedsClarificationAsString_IsReadLeniently()
    {
        LlmReplies("""{"intent":"Other","confidence":"low","summary":"x","needsClarification":"true","clarificationQuestion":"Kommst du heute?"}""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.NeedsClarification.ShouldBeTrue();
        result.FailureReason.ShouldBeNull();
    }

    [Test]
    public async Task NeedsClarificationFalse_DropsTheQuestion()
    {
        LlmReplies("""{"intent":"VacationRequest","confidence":"high","summary":"Ferien","fromDate":"2026-08-03","untilDate":"2026-08-14","needsClarification":false,"clarificationQuestion":"Wirklich?"}""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.NeedsClarification.ShouldBeFalse();
        result.ClarificationQuestion.ShouldBeNull();
    }

    [Test]
    public async Task Customer_NeverNeedsClarification()
    {
        LlmReplies("""{"intent":"CustomerMessage","summary":"Kunde","needsClarification":true,"clarificationQuestion":"Was meinen Sie?"}""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Customer, Source());

        result.NeedsClarification.ShouldBeFalse();
        result.ClarificationQuestion.ShouldBeNull();
    }

    [Test]
    public async Task UndatedWorkCancellation_DefaultsToCompanyLocalReceivedDay_WithLowConfidence()
    {
        _companyClock.TimeZone = FixedOffsetZone(2);
        LlmReplies("""{"intent":"WorkCancellation","confidence":"high","summary":"Ich bin krank.","fromDate":null,"untilDate":null}""");
        var source = Source() with { ReceivedAt = new DateTime(2026, 7, 8, 23, 30, 0, DateTimeKind.Utc) };

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, source);

        result.FromDate.ShouldBe(new DateOnly(2026, 7, 9));
        result.UntilDate.ShouldBe(new DateOnly(2026, 7, 9));
        result.Confidence.ShouldBe(EmailConfidence.Low);
    }

    [Test]
    public async Task UndatedDayOffWish_KeepsNullDates()
    {
        LlmReplies("""{"intent":"DayOffWish","confidence":"high","summary":"Frei","fromDate":null,"untilDate":null}""");

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.FromDate.ShouldBeNull();
        result.Confidence.ShouldBe(EmailConfidence.High);
    }

    [Test]
    public void SystemPrompt_AsksForTheClarificationFields()
    {
        var prompt = InboundIntentAnalysisService.BuildPrompt(
            Source(), EntityTypeEnum.Employee, "body", new DateOnly(2026, 7, 8), DefaultKeywords);

        prompt.SystemPrompt.ShouldContain("\"needsClarification\"");
        prompt.SystemPrompt.ShouldContain("\"clarificationQuestion\"");
        prompt.SystemPrompt.ShouldContain("never ask about symptoms");
    }

    [Test]
    public async Task AnalyzeAnswerAsync_SendsOriginalQuestionAndAnswer_WithOriginalDateLine()
    {
        string? capturedSystem = null;
        string? capturedUser = null;
        _completionService.CompleteAsync(
                Arg.Do<string>(s => capturedSystem = s), Arg.Do<string>(u => capturedUser = u),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(OneShotCompletionResult.Succeeded(
                """{"intent":"WorkCancellation","confidence":"high","summary":"Kann heute nicht.","fromDate":"2026-07-08","untilDate":"2026-07-08","needsClarification":false}"""));

        await _service.AnalyzeAnswerAsync(ClientId, EntityTypeEnum.Employee, AnswerSource(), History());

        capturedUser.ShouldNotBeNull();
        capturedUser.ShouldContain("Original message (Date: 2026-07-08 (Wednesday)): Ich fühle mich nicht gut.");
        capturedUser.ShouldContain("Question from the planning assistant: Heißt das, du kannst heute deinen Spätdienst (14:00-22:00) nicht antreten?");
        capturedUser.ShouldContain("Answer (Date: 2026-07-08 (Wednesday)): Ja, leider.");
        capturedSystem.ShouldNotBeNull();
        capturedSystem.ShouldContain("no further question will be sent");
    }

    [Test]
    public async Task AnalyzeAnswerAsync_ResultBelongsToTheAnswerSource()
    {
        LlmReplies("""{"intent":"WorkCancellation","confidence":"high","summary":"Kann heute nicht.","fromDate":"2026-07-08","untilDate":"2026-07-08","needsClarification":false}""");
        var answer = AnswerSource();

        var result = await _service.AnalyzeAnswerAsync(ClientId, EntityTypeEnum.Employee, answer, History());

        result.SourceKind.ShouldBe(InboundSourceKind.Messenger);
        result.SourceId.ShouldBe(answer.SourceId);
        result.Intent.ShouldBe(EmailIntent.WorkCancellation);
        result.NeedsClarification.ShouldBeFalse();
    }

    [Test]
    public async Task AnalyzeAnswerAsync_UndatedCancellation_DefaultsToTheOriginalDay()
    {
        _companyClock.TimeZone = FixedOffsetZone(2);
        LlmReplies("""{"intent":"WorkCancellation","confidence":"high","summary":"Ja.","fromDate":null,"untilDate":null,"needsClarification":false}""");
        var lateAnswer = AnswerSource() with { ReceivedAt = new DateTime(2026, 7, 8, 23, 0, 0, DateTimeKind.Utc) };

        var result = await _service.AnalyzeAnswerAsync(ClientId, EntityTypeEnum.Employee, lateAnswer, History());

        result.FromDate.ShouldBe(new DateOnly(2026, 7, 8));
        result.Confidence.ShouldBe(EmailConfidence.Low);
    }

    [Test]
    public async Task AnalyzeAnswerAsync_LlmCallFails_DegradesWithoutException()
    {
        CompletionReturns(OneShotCompletionResult.Failed("provider down"));

        var result = await _service.AnalyzeAnswerAsync(ClientId, EntityTypeEnum.Employee, AnswerSource(), History());

        result.Intent.ShouldBe(EmailIntent.Other);
        result.FailureReason.ShouldNotBeNull();
        result.FailureReason.ShouldContain("provider down");
    }
}
