// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for InboundIntentAnalysisService — verifies customer-fixed intent, LLM JSON parsing
/// (clean, embedded and broken replies) and that an LLM failure degrades to a recorded failure
/// instead of an exception. Client resolution and the enabled/disabled feature gate are the caller's
/// responsibility and are exercised in EmailPollingBackgroundServiceTests instead.
/// </summary>

using Klacks.Api.Application.Interfaces;
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

    private ILLMService _llmService = null!;
    private IScheduleCommandKeywordProvider _keywordProvider = null!;
    private InboundIntentAnalysisService _service = null!;

    private static readonly Guid ClientId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _llmService = Substitute.For<ILLMService>();
        _keywordProvider = Substitute.For<IScheduleCommandKeywordProvider>();
        _keywordProvider.GetAsync(Arg.Any<CancellationToken>()).Returns(DefaultKeywords);

        _service = new InboundIntentAnalysisService(
            Substitute.For<IPlanningAudienceResolver>(), _llmService,
            _keywordProvider, Substitute.For<ILogger<InboundIntentAnalysisService>>());
    }

    private void LlmReplies(string message)
    {
        _llmService.ProcessAsync(Arg.Any<LLMContext>())
            .Returns(new LLMResponse { Message = message });
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
        _llmService.ProcessAsync(Arg.Any<LLMContext>())
            .Returns<LLMResponse>(_ => throw new InvalidOperationException("provider down"));

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

        var prompt = InboundIntentAnalysisService.BuildPrompt(Source(), EntityTypeEnum.Employee, "body", keywords);

        prompt.ShouldContain("URLAUB");
        prompt.ShouldContain("KEINE_NACHT");
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
        _llmService.ProcessAsync(Arg.Any<LLMContext>())
            .Returns<LLMResponse>(_ => throw new InvalidOperationException("provider down"));

        var result = await _service.AnalyzeAsync(ClientId, EntityTypeEnum.Employee, Source());

        result.Confidence.ShouldBe(EmailConfidence.Low);
    }
}
