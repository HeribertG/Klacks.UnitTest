// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for EmailIntentAnalysisService — verifies the feature gate, the sender resolution
/// gate, customer-fixed intent, LLM JSON parsing (clean, embedded and broken replies) and that
/// an LLM failure degrades to a recorded failure instead of an exception.
/// </summary>

using Klacks.Api.Application.Interfaces;
using AppSettings = Klacks.Api.Application.Constants.Settings;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Email;
using Klacks.Api.Infrastructure.Email;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Email;

[TestFixture]
public class EmailIntentAnalysisServiceTests
{
    private IEmailClientAssignmentService _assignmentService = null!;
    private ILLMService _llmService = null!;
    private ISettingsRepository _settingsRepository = null!;
    private EmailIntentAnalysisService _service = null!;

    private static readonly Guid ClientId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _assignmentService = Substitute.For<IEmailClientAssignmentService>();
        _llmService = Substitute.For<ILLMService>();
        _settingsRepository = Substitute.For<ISettingsRepository>();

        EnableFeature(true);

        _service = new EmailIntentAnalysisService(
            _assignmentService, _llmService, _settingsRepository,
            Substitute.For<ILogger<EmailIntentAnalysisService>>());
    }

    private void EnableFeature(bool enabled)
    {
        _settingsRepository.GetSetting(AppSettings.EMAIL_ANALYSIS_ENABLED)
            .Returns(new Klacks.Api.Domain.Models.Settings.Settings
            {
                Type = AppSettings.EMAIL_ANALYSIS_ENABLED,
                Value = enabled ? "true" : "false"
            });
    }

    private void ResolvesTo(EntityTypeEnum type)
    {
        _assignmentService.ResolveClientAsync(Arg.Any<ReceivedEmail>(), Arg.Any<CancellationToken>())
            .Returns((ClientId, type));
    }

    private void LlmReplies(string message)
    {
        _llmService.ProcessAsync(Arg.Any<LLMContext>())
            .Returns(new LLMResponse { Message = message });
    }

    private static ReceivedEmail Email() => new()
    {
        Id = Guid.NewGuid(),
        FromAddress = "worker@example.com",
        Subject = "Krankmeldung",
        BodyText = "Ich bin krank und kann morgen nicht arbeiten.",
        ReceivedDate = new DateTime(2026, 7, 8, 8, 0, 0, DateTimeKind.Utc)
    };

    [Test]
    public async Task FeatureDisabled_ReturnsNull_WithoutResolvingOrLlm()
    {
        EnableFeature(false);

        var result = await _service.AnalyzeAsync(Email());

        result.ShouldBeNull();
        await _assignmentService.DidNotReceiveWithAnyArgs().ResolveClientAsync(default!, default);
        await _llmService.DidNotReceiveWithAnyArgs().ProcessAsync(default!);
    }

    [Test]
    public async Task UnknownSender_ReturnsNull_WithoutLlmCall()
    {
        _assignmentService.ResolveClientAsync(Arg.Any<ReceivedEmail>(), Arg.Any<CancellationToken>())
            .Returns(((Guid, EntityTypeEnum)?)null);

        var result = await _service.AnalyzeAsync(Email());

        result.ShouldBeNull();
        await _llmService.DidNotReceiveWithAnyArgs().ProcessAsync(default!);
    }

    [Test]
    public async Task EmployeeWorkCancellation_ParsesIntentSummaryAndDates()
    {
        ResolvesTo(EntityTypeEnum.Employee);
        LlmReplies("""{"intent":"WorkCancellation","summary":"Mitarbeiter meldet sich krank.","fromDate":"2026-07-09","untilDate":"2026-07-10"}""");

        var email = Email();
        var result = await _service.AnalyzeAsync(email);

        result.ShouldNotBeNull();
        result!.ReceivedEmailId.ShouldBe(email.Id);
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
        ResolvesTo(EntityTypeEnum.ExternEmp);
        LlmReplies("""Here is the analysis: {"intent":"VacationRequest","summary":"Ferien im August.","fromDate":"2026-08-03","untilDate":"2026-08-14"} Done.""");

        var result = await _service.AnalyzeAsync(Email());

        result.ShouldNotBeNull();
        result!.Intent.ShouldBe(EmailIntent.VacationRequest);
        result.FromDate.ShouldBe(new DateOnly(2026, 8, 3));
    }

    [Test]
    public async Task NullDates_MapToNull()
    {
        ResolvesTo(EntityTypeEnum.Employee);
        LlmReplies("""{"intent":"DayOffWish","summary":"Wunsch nach freien Tagen.","fromDate":null,"untilDate":null}""");

        var result = await _service.AnalyzeAsync(Email());

        result.ShouldNotBeNull();
        result!.Intent.ShouldBe(EmailIntent.DayOffWish);
        result.FromDate.ShouldBeNull();
        result.UntilDate.ShouldBeNull();
    }

    [Test]
    public async Task Customer_AlwaysCustomerMessage_EvenIfLlmSaysOtherwise()
    {
        ResolvesTo(EntityTypeEnum.Customer);
        LlmReplies("""{"intent":"WorkCancellation","summary":"Kunde schreibt etwas.","fromDate":null,"untilDate":null}""");

        var result = await _service.AnalyzeAsync(Email());

        result.ShouldNotBeNull();
        result!.Intent.ShouldBe(EmailIntent.CustomerMessage);
        result.Summary.ShouldBe("Kunde schreibt etwas.");
    }

    [Test]
    public async Task UnparsableReply_DegradesToOther_WithFailureReason()
    {
        ResolvesTo(EntityTypeEnum.Employee);
        LlmReplies("Sorry, I cannot help with that.");

        var result = await _service.AnalyzeAsync(Email());

        result.ShouldNotBeNull();
        result!.Intent.ShouldBe(EmailIntent.Other);
        result.FailureReason.ShouldNotBeNull();
    }

    [Test]
    public async Task LlmThrows_DegradesToFailureAnalysis_NoException()
    {
        ResolvesTo(EntityTypeEnum.Employee);
        _llmService.ProcessAsync(Arg.Any<LLMContext>())
            .Returns<LLMResponse>(_ => throw new InvalidOperationException("provider down"));

        var email = Email();
        var result = await _service.AnalyzeAsync(email);

        result.ShouldNotBeNull();
        result!.Intent.ShouldBe(EmailIntent.Other);
        result.Summary.ShouldBe(email.Subject);
        result.FailureReason.ShouldBe("provider down");
    }

    [Test]
    public async Task UnknownIntentString_MapsToOther()
    {
        ResolvesTo(EntityTypeEnum.Employee);
        LlmReplies("""{"intent":"SomethingNew","summary":"Unklar.","fromDate":null,"untilDate":null}""");

        var result = await _service.AnalyzeAsync(Email());

        result.ShouldNotBeNull();
        result!.Intent.ShouldBe(EmailIntent.Other);
        result.FailureReason.ShouldBeNull();
    }
}
