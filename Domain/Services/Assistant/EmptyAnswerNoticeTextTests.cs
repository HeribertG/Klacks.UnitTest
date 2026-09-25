// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// EmptyAnswerNoticeText recognizes a stored empty-answer notice in every language Klacks ships - the four
/// core languages and all 21 packs as the startup loader configures them - on its own and after streamed
/// prose, and swaps it for the state marker that says what happened. Ordinary text and the other canned
/// replies (recipe buttons, the clarification question) pass through unchanged. The compaction input carries
/// the marker, never the notice.
/// </summary>

using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Constants;
using Microsoft.Extensions.Logging;
using LlmProviders = Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class EmptyAnswerNoticeTextTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string PluginsDirectory = "Plugins";
    private const string LanguagesDirectory = "Languages";
    private const int ShippedLanguages = 25;
    private const string Prose = "Let me check that quickly.";
    private const string OrdinaryAnswer = "Anna works in the groups Bern and Basel.";
    private const string ConversationId = "conv-notice";
    private const string OwnerId = "owner-notice";

    [TearDown]
    public void ResetConfiguredTexts()
    {
        GracefulCorrectionTexts.Reset();
        ClarificationTexts.Reset();
        EscalationHandoffTexts.Reset();
        MessengerProactiveTexts.Reset();
    }

    [Test]
    public void EveryShippedLanguage_FallbackNotice_BecomesTheStepsRanMarker()
    {
        var notices = LoadedTextsOf(GracefulCorrectionTexts.EmptyAnswerFallbackNotice);

        notices.Count.ShouldBe(ShippedLanguages);
        notices.Where(notice => EmptyAnswerNoticeText.ToStateMarkers(notice) != EmptyAnswerRecoveryConstants.StepsRanStateMarker)
            .ShouldBeEmpty();
    }

    [Test]
    public void EveryShippedLanguage_NoActionNotice_BecomesTheNothingExecutedMarker()
    {
        var notices = LoadedTextsOf(GracefulCorrectionTexts.EmptyAnswerNoActionNotice);

        notices.Count.ShouldBe(ShippedLanguages);
        notices.Where(notice => EmptyAnswerNoticeText.ToStateMarkers(notice) != EmptyAnswerRecoveryConstants.NothingExecutedStateMarker)
            .ShouldBeEmpty();
    }

    [Test]
    public void NoticeAfterStreamedProse_KeepsTheProseAndReplacesOnlyTheNotice()
    {
        var stored = Prose + EmptyAnswerRecoveryConstants.AppendedAnswerSeparator + EmptyAnswerRecoveryConstants.FallbackNotice;

        EmptyAnswerNoticeText.ToStateMarkers(stored).ShouldBe(
            Prose + EmptyAnswerRecoveryConstants.AppendedAnswerSeparator + EmptyAnswerRecoveryConstants.StepsRanStateMarker);
    }

    [Test]
    public void OrdinaryAnswer_IsUnchanged()
    {
        EmptyAnswerNoticeText.ToStateMarkers(OrdinaryAnswer).ShouldBe(OrdinaryAnswer);
    }

    [Test]
    public void OtherCannedReplies_AreUnchanged()
    {
        AssistantTextsPluginLoader.Load(ApiRoot());
        var otherKeys = GracefulCorrectionTexts.RequiredKeys
            .Except([GracefulCorrectionTexts.EmptyAnswerFallbackNotice, GracefulCorrectionTexts.EmptyAnswerNoActionNotice]);

        var altered = otherKeys
            .SelectMany(GracefulCorrectionTexts.AllTextsOf)
            .Where(text => EmptyAnswerNoticeText.ToStateMarkers(text) != text)
            .ToList();

        altered.ShouldBeEmpty();
    }

    [Test]
    public async Task CompactionInput_CarriesTheMarkerInsteadOfTheNotice()
    {
        AssistantTextsPluginLoader.Load(ApiRoot());
        GracefulCorrectionTexts.TryGetText(GracefulCorrectionTexts.EmptyAnswerNoActionNotice, "ja", out var japaneseNotice)
            .ShouldBeTrue();
        var request = await CompactionRequestForAsync(new List<LLMMessage>
        {
            new() { Role = "user", Content = "Plane die Woche." },
            new() { Role = "assistant", Content = EmptyAnswerRecoveryConstants.FallbackNotice },
            new() { Role = "user", Content = "Nochmal bitte." },
            new() { Role = "assistant", Content = japaneseNotice },
            new() { Role = "user", Content = "Und jetzt?" },
            new() { Role = "assistant", Content = OrdinaryAnswer }
        });

        request.Message.ShouldNotContain(EmptyAnswerRecoveryConstants.FallbackNotice);
        request.Message.ShouldNotContain(japaneseNotice);
        request.Message.ShouldContain(EmptyAnswerRecoveryConstants.StepsRanStateMarker);
        request.Message.ShouldContain(EmptyAnswerRecoveryConstants.NothingExecutedStateMarker);
        request.Message.ShouldContain(OrdinaryAnswer);
    }

    private static List<string> LoadedTextsOf(string key)
    {
        var failures = new List<string>();
        AssistantTextsPluginLoader.Load(ApiRoot(), (file, ex) => failures.Add($"{file}: {ex.Message}"));
        failures.ShouldBeEmpty();
        return GracefulCorrectionTexts.AllTextsOf(key).ToList();
    }

    private static async Task<LlmProviders.LLMProviderRequest> CompactionRequestForAsync(List<LLMMessage> oldMessages)
    {
        var repository = Substitute.For<ILLMRepository>();
        repository.GetConversationByConversationIdAsync(ConversationId, OwnerId)
            .Returns(new LLMConversation { ConversationId = ConversationId, UserId = OwnerId, MessageCount = 40 });
        repository.GetOldestMessagesAsync(ConversationId, OwnerId, Arg.Any<int>(), Arg.Any<int>()).Returns(oldMessages);

        LlmProviders.LLMProviderRequest? captured = null;
        var provider = Substitute.For<LlmProviders.ILLMProvider>();
        provider.ProcessAsync(Arg.Any<LlmProviders.LLMProviderRequest>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                captured = call.Arg<LlmProviders.LLMProviderRequest>();
                return new LlmProviders.LLMProviderResponse { Success = true, Content = "{}" };
            });
        var resolver = Substitute.For<ICheapestModelResolver>();
        resolver.ResolveAsync(Arg.Any<CancellationToken>())
            .Returns(((LLMModel?)new LLMModel { ModelId = "m", ApiModelId = "m" }, (LlmProviders.ILLMProvider?)provider));

        await new ConversationCompactionService(
                Substitute.For<ILogger<ConversationCompactionService>>(), resolver, repository)
            .CompactIfNeededAsync(ConversationId, OwnerId);

        captured.ShouldNotBeNull();
        return captured!;
    }

    private static string ApiRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (Directory.Exists(Path.Combine(candidate, PluginsDirectory, LanguagesDirectory)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {ApiProjectDirectory}/{PluginsDirectory}/{LanguagesDirectory} from the test base directory.");
    }
}
