// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using AppSettings = Klacks.Api.Application.Constants.Settings;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Email;
using Klacks.Api.Infrastructure.Email;
using Microsoft.Extensions.Logging;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Email;

[TestFixture]
public class SpamFilterServiceTests
{
    private ISpamRuleRepository _spamRuleRepository = null!;
    private ISettingsRepository _settingsRepository = null!;
    private IOneShotCompletionService _completionService = null!;
    private SpamFilterService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _spamRuleRepository = Substitute.For<ISpamRuleRepository>();
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _completionService = Substitute.For<IOneShotCompletionService>();

        _spamRuleRepository.GetAllActiveAsync().Returns(new List<SpamRule>());

        _service = new SpamFilterService(
            _spamRuleRepository, _settingsRepository, _completionService,
            Substitute.For<ILogger<SpamFilterService>>());
    }

    private void Rules(params SpamRule[] rules) =>
        _spamRuleRepository.GetAllActiveAsync().Returns(rules.ToList());

    private void SetSetting(string key, string? value) =>
        _settingsRepository.GetSetting(key).Returns(value == null ? null : new SettingsModel { Type = key, Value = value });

    private void LlmReplies(string message) => CompletionReturns(OneShotCompletionResult.Succeeded(message));

    private void CompletionReturns(OneShotCompletionResult result) =>
        _completionService.CompleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(result);

    private Task CompletionWasNotCalled() =>
        _completionService.DidNotReceiveWithAnyArgs().CompleteAsync(default!, default!, default, default);

    private static SpamRule Rule(SpamRuleType type, string pattern) =>
        new() { RuleType = type, Pattern = pattern, IsActive = true };

    private static ReceivedEmail Email(
        string fromAddress = "someone@example.com",
        string? fromName = null,
        string subject = "Hello",
        string? bodyText = "Just a normal message.",
        string? bodyHtml = null) => new()
        {
            Id = Guid.NewGuid(),
            FromAddress = fromAddress,
            FromName = fromName,
            Subject = subject,
            BodyText = bodyText,
            BodyHtml = bodyHtml,
            ReceivedDate = new DateTime(2026, 7, 8, 8, 0, 0, DateTimeKind.Utc)
        };

    [Test]
    public async Task MatchingRule_ReturnsSpam_WithReasonAndNoLlm()
    {
        Rules(Rule(SpamRuleType.SubjectContains, "viagra"));

        var result = await _service.ClassifyAsync(Email(subject: "Buy Viagra now"));

        result.Score.ShouldBe(1.0f);
        result.IsSpam.ShouldBeTrue();
        result.UsedLlm.ShouldBeFalse();
        result.Reason.ShouldBe("Matched rule: SubjectContains with pattern 'viagra'");
        await CompletionWasNotCalled();
    }

    [Test]
    public async Task NoRuleMatches_ReturnsHam_WithoutLlm()
    {
        Rules(Rule(SpamRuleType.SubjectContains, "viagra"));

        var result = await _service.ClassifyAsync(Email(subject: "Meeting tomorrow"));

        result.Score.ShouldBe(0.0f);
        result.IsSpam.ShouldBeFalse();
        result.UsedLlm.ShouldBeFalse();
        result.Reason.ShouldBe("No rule matched");
    }

    [Test]
    public async Task ScoreAtOrAboveSpamThreshold_IsSpam_EvenWithoutLlm()
    {
        Rules(Rule(SpamRuleType.SubjectContains, "viagra"));
        SetSetting(AppSettings.SPAM_FILTER_SPAM_THRESHOLD, "1.0");
        SetSetting(AppSettings.SPAM_FILTER_LLM_ENABLED, "true");

        var result = await _service.ClassifyAsync(Email(subject: "Viagra deal"));

        result.IsSpam.ShouldBeTrue();
        result.UsedLlm.ShouldBeFalse();
        await CompletionWasNotCalled();
    }

    [Test]
    public async Task ScoreBelowUncertainThreshold_IsHam_EvenWithLlmEnabled()
    {
        SetSetting(AppSettings.SPAM_FILTER_LLM_ENABLED, "true");

        var result = await _service.ClassifyAsync(Email());

        result.IsSpam.ShouldBeFalse();
        result.UsedLlm.ShouldBeFalse();
        await CompletionWasNotCalled();
    }

    [Test]
    public async Task UncertainRangeWithLlmDisabled_IsHam_WithoutLlmCall()
    {
        SetSetting(AppSettings.SPAM_FILTER_UNCERTAIN_THRESHOLD, "-0.1");
        SetSetting(AppSettings.SPAM_FILTER_LLM_ENABLED, "false");

        var result = await _service.ClassifyAsync(Email());

        result.Score.ShouldBe(0.0f);
        result.IsSpam.ShouldBeFalse();
        result.UsedLlm.ShouldBeFalse();
        await CompletionWasNotCalled();
    }

    [Test]
    public async Task UncertainRangeWithLlmEnabled_AndLlmSaysSpam_ReturnsSpamFromLlm()
    {
        SetSetting(AppSettings.SPAM_FILTER_UNCERTAIN_THRESHOLD, "-0.1");
        SetSetting(AppSettings.SPAM_FILTER_LLM_ENABLED, "true");
        LlmReplies("This looks like SPAM to me.");

        var result = await _service.ClassifyAsync(Email());

        result.Score.ShouldBe(0.9f);
        result.IsSpam.ShouldBeTrue();
        result.UsedLlm.ShouldBeTrue();
        result.Reason.ShouldBe("LLM classified as SPAM");
    }

    [Test]
    public async Task UncertainRangeWithLlmEnabled_AndLlmSaysHam_ReturnsHamFromLlm()
    {
        SetSetting(AppSettings.SPAM_FILTER_UNCERTAIN_THRESHOLD, "-0.1");
        SetSetting(AppSettings.SPAM_FILTER_LLM_ENABLED, "true");
        LlmReplies("This is HAM, a legitimate message.");

        var result = await _service.ClassifyAsync(Email());

        result.Score.ShouldBe(0.1f);
        result.IsSpam.ShouldBeFalse();
        result.UsedLlm.ShouldBeTrue();
        result.Reason.ShouldBe("LLM classified as HAM");
    }

    [Test]
    public async Task UncertainRangeViaSpamThresholdAboveOne_ForMatchedRule_CallsLlm()
    {
        Rules(Rule(SpamRuleType.SubjectContains, "viagra"));
        SetSetting(AppSettings.SPAM_FILTER_SPAM_THRESHOLD, "1.1");
        SetSetting(AppSettings.SPAM_FILTER_LLM_ENABLED, "true");
        LlmReplies("SPAM");

        var result = await _service.ClassifyAsync(Email(subject: "Viagra deal"));

        result.UsedLlm.ShouldBeTrue();
        result.IsSpam.ShouldBeTrue();
        result.Score.ShouldBe(0.9f);
        await _completionService.Received(1).CompleteAsync(
            Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LlmThrows_FallsBackToRuleResult_WithFallbackReasonAppended()
    {
        Rules(Rule(SpamRuleType.SubjectContains, "viagra"));
        SetSetting(AppSettings.SPAM_FILTER_SPAM_THRESHOLD, "1.1");
        SetSetting(AppSettings.SPAM_FILTER_LLM_ENABLED, "true");
        _completionService.CompleteAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns<OneShotCompletionResult>(_ => throw new InvalidOperationException("provider down"));

        var result = await _service.ClassifyAsync(Email(subject: "Viagra deal"));

        result.Score.ShouldBe(1.0f);
        result.IsSpam.ShouldBeTrue();
        result.Reason.ShouldBe("Matched rule: SubjectContains with pattern 'viagra' (LLM fallback failed)");
    }

    [Test]
    public async Task SenderContains_MatchesFromAddress()
    {
        Rules(Rule(SpamRuleType.SenderContains, "spammy"));

        var result = await _service.ClassifyAsync(Email(fromAddress: "SpAmMy@example.com"));

        result.IsSpam.ShouldBeTrue();
    }

    [Test]
    public async Task SenderContains_MatchesFromName()
    {
        Rules(Rule(SpamRuleType.SenderContains, "lottery"));

        var result = await _service.ClassifyAsync(Email(fromName: "Lottery Winner"));

        result.IsSpam.ShouldBeTrue();
    }

    [Test]
    public async Task SenderDomain_MatchesExactDomain_CaseInsensitive()
    {
        Rules(Rule(SpamRuleType.SenderDomain, "spam.com"));

        var result = await _service.ClassifyAsync(Email(fromAddress: "user@SPAM.COM"));

        result.IsSpam.ShouldBeTrue();
    }

    [Test]
    public async Task SenderDomain_DoesNotMatchSubstring()
    {
        Rules(Rule(SpamRuleType.SenderDomain, "spam.com"));

        var result = await _service.ClassifyAsync(Email(fromAddress: "user@notspam.com"));

        result.IsSpam.ShouldBeFalse();
    }

    [Test]
    public async Task SubjectContains_IsCaseInsensitiveSubstring()
    {
        Rules(Rule(SpamRuleType.SubjectContains, "free money"));

        var result = await _service.ClassifyAsync(Email(subject: "Get your FREE MONEY today"));

        result.IsSpam.ShouldBeTrue();
    }

    [Test]
    public async Task BodyContains_MatchesBodyText()
    {
        Rules(Rule(SpamRuleType.BodyContains, "click here"));

        var result = await _service.ClassifyAsync(Email(bodyText: "Please Click Here to claim your prize."));

        result.IsSpam.ShouldBeTrue();
    }

    [Test]
    public async Task BodyContains_MatchesBodyHtml_WhenBodyTextDoesNotContainPattern()
    {
        Rules(Rule(SpamRuleType.BodyContains, "click here"));

        var result = await _service.ClassifyAsync(Email(bodyText: "Plain text.", bodyHtml: "<p>Click Here now</p>"));

        result.IsSpam.ShouldBeTrue();
    }

    [Test]
    public async Task MissingSetting_FallsBackToDefaultThresholds()
    {
        SetSetting(AppSettings.SPAM_FILTER_SPAM_THRESHOLD, null);
        SetSetting(AppSettings.SPAM_FILTER_UNCERTAIN_THRESHOLD, null);

        var result = await _service.ClassifyAsync(Email());

        result.IsSpam.ShouldBeFalse();
        result.UsedLlm.ShouldBeFalse();
    }

    [Test]
    public async Task UnparsableSetting_FallsBackToDefaultThresholds()
    {
        Rules(Rule(SpamRuleType.SubjectContains, "viagra"));
        SetSetting(AppSettings.SPAM_FILTER_SPAM_THRESHOLD, "not-a-float");

        var result = await _service.ClassifyAsync(Email(subject: "Viagra deal"));

        result.Score.ShouldBe(1.0f);
        result.IsSpam.ShouldBeTrue();
        result.UsedLlm.ShouldBeFalse();
    }

    [Test]
    public async Task UnparsableLlmEnabledSetting_FallsBackToDisabled()
    {
        SetSetting(AppSettings.SPAM_FILTER_UNCERTAIN_THRESHOLD, "-0.1");
        SetSetting(AppSettings.SPAM_FILTER_LLM_ENABLED, "not-a-bool");

        var result = await _service.ClassifyAsync(Email());

        result.IsSpam.ShouldBeFalse();
        result.UsedLlm.ShouldBeFalse();
        await CompletionWasNotCalled();
    }

    [Test]
    public async Task LlmCallFailed_FallsBackToRuleResult_InsteadOfConfidentHam()
    {
        SetSetting(AppSettings.SPAM_FILTER_UNCERTAIN_THRESHOLD, "-0.1");
        SetSetting(AppSettings.SPAM_FILTER_LLM_ENABLED, "true");
        CompletionReturns(OneShotCompletionResult.Failed("Invalid API key"));

        var result = await _service.ClassifyAsync(Email());

        result.UsedLlm.ShouldBeFalse();
        result.IsSpam.ShouldBeFalse();
        result.Score.ShouldBe(0.0f);
        result.Reason.ShouldBe("No rule matched (LLM fallback failed)");
    }

    [Test]
    public async Task LlmCallFailed_ForMatchedRule_KeepsRuleSpamVerdict()
    {
        Rules(Rule(SpamRuleType.SubjectContains, "viagra"));
        SetSetting(AppSettings.SPAM_FILTER_SPAM_THRESHOLD, "1.1");
        SetSetting(AppSettings.SPAM_FILTER_LLM_ENABLED, "true");
        CompletionReturns(OneShotCompletionResult.Failed("503 Service Unavailable"));

        var result = await _service.ClassifyAsync(Email(subject: "Viagra deal"));

        result.UsedLlm.ShouldBeFalse();
        result.IsSpam.ShouldBeTrue();
        result.Score.ShouldBe(1.0f);
        result.Reason.ShouldBe("Matched rule: SubjectContains with pattern 'viagra' (LLM fallback failed)");
    }

    [Test]
    public async Task LlmReturnsEmptyContent_FallsBackToRuleResult()
    {
        SetSetting(AppSettings.SPAM_FILTER_UNCERTAIN_THRESHOLD, "-0.1");
        SetSetting(AppSettings.SPAM_FILTER_LLM_ENABLED, "true");
        LlmReplies("   ");

        var result = await _service.ClassifyAsync(Email());

        result.UsedLlm.ShouldBeFalse();
        result.Reason.ShouldBe("No rule matched (LLM fallback failed)");
    }

    [Test]
    public async Task LlmCall_SendsInstructionsAsSystemPrompt_AndTheMailAsUserMessage()
    {
        SetSetting(AppSettings.SPAM_FILTER_UNCERTAIN_THRESHOLD, "-0.1");
        SetSetting(AppSettings.SPAM_FILTER_LLM_ENABLED, "true");
        string? capturedSystemPrompt = null;
        string? capturedUserMessage = null;
        _completionService.CompleteAsync(
                Arg.Do<string>(sp => capturedSystemPrompt = sp), Arg.Do<string>(um => capturedUserMessage = um),
                Arg.Any<string?>(), Arg.Any<CancellationToken>())
            .Returns(OneShotCompletionResult.Succeeded("HAM"));

        await _service.ClassifyAsync(Email(subject: "Meeting tomorrow"));

        capturedSystemPrompt.ShouldNotBeNull();
        capturedSystemPrompt!.ShouldContain("SPAM or HAM");
        capturedUserMessage.ShouldNotBeNull();
        capturedUserMessage!.ShouldContain("Subject: Meeting tomorrow");
        capturedUserMessage.ShouldNotContain("Classify");
    }

    [Test]
    public void Constructor_DoesNotTakeILLMService_BecauseTheChatPipelineRanRecipesAndMemoryOnForeignText()
    {
        var parameterTypes = typeof(SpamFilterService).GetConstructors()
            .SelectMany(c => c.GetParameters())
            .Select(p => p.ParameterType)
            .ToList();

        parameterTypes.ShouldNotContain(typeof(ILLMService));
        parameterTypes.ShouldContain(typeof(IOneShotCompletionService));
    }
}
