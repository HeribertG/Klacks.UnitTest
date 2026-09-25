// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// EscalationNotifier.NotifyHandoffAsync and NotifyExhaustedAsync in the installation language: each of the
/// four core languages writes its own sentences, a language-pack language (ja, and zh-CN in any casing)
/// writes the sentences of its assistant-texts.json, an unknown or unset language writes English, the
/// confirmation goes to the acknowledging planner (and over the messenger for an absence chain only), the
/// quiet note only to the stages notified before them, and a value that looks like a placeholder (a planner
/// display name, a finding kind) is never expanded a second time.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Assistant.Escalation;
using Klacks.Api.Infrastructure.Services.Assistant.Escalation;
using Klacks.Api.Infrastructure.Services.Settings;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant.Escalation;

[TestFixture]
public class EscalationNotifierHandoffTextTests
{
    private const string AcknowledgingUserId = "planner-ack";
    private const string EarlierUserId = "planner-earlier";
    private const string AcknowledgingName = "Ann Acknowledger";
    private const string AbsentEmployee = "Erika Absent";
    private const string TriggerKind = "empty_container";
    private const string UnknownAction = "unknown action";
    private const string Japanese = "ja";
    private const string ChineseSimplified = "zh-CN";
    private const string ApiDirectory = "Klacks.Api";
    private const string PluginsDirectory = "Plugins";
    private const string LanguagesDirectory = "Languages";
    private const string AssistantTextsFile = "assistant-texts.json";
    private const string ShiftDate = "16.08.2026";

    private static readonly Guid ConditionId = Guid.NewGuid();
    private static readonly DateTime ShiftStartUtc = new(2026, 8, 16, 6, 0, 0, DateTimeKind.Utc);

    private IProactiveTriggerDispatchRepository _dispatchRepository = null!;
    private IOfflineMessengerNotifier _messenger = null!;
    private IAgentConditionRepository _conditionRepository = null!;
    private ISettingsReader _settingsReader = null!;
    private List<ProactiveTriggerDispatchRow> _rows = null!;
    private EscalationNotifier _sut = null!;

    [SetUp]
    public void SetUp()
    {
        EscalationHandoffTexts.Reset();
        _rows = [];
        _dispatchRepository = Substitute.For<IProactiveTriggerDispatchRepository>();
        _dispatchRepository
            .When(repository => repository.RecordAsync(Arg.Any<ProactiveTriggerDispatchRow>(), Arg.Any<CancellationToken>()))
            .Do(call => _rows.Add(call.Arg<ProactiveTriggerDispatchRow>()));

        var notificationService = Substitute.For<IAssistantNotificationService>();
        notificationService.GetConnectedUserIdsAsync().Returns(new List<string>());
        _messenger = Substitute.For<IOfflineMessengerNotifier>();
        _conditionRepository = Substitute.For<IAgentConditionRepository>();
        _conditionRepository.GetByIdAsync(ConditionId, Arg.Any<CancellationToken>())
            .Returns(new AgentCondition { Id = ConditionId, TriggerKind = TriggerKind });

        _settingsReader = Substitute.For<ISettingsReader>();
        UseLanguage(null);

        _sut = new EscalationNotifier(
            _dispatchRepository,
            notificationService,
            _messenger,
            Substitute.For<IProactiveMessengerTextComposer>(),
            new EscalationHandoffTextService(
                new InstallationLanguageResolver(_settingsReader, Substitute.For<ILogger<InstallationLanguageResolver>>()),
                Substitute.For<ILogger<EscalationHandoffTextService>>()),
            new FixedCompanyClock(new DateTimeOffset(ShiftStartUtc, TimeSpan.Zero)),
            _conditionRepository,
            Substitute.For<IConditionRemediationRegistry>(),
            Substitute.For<ILogger<EscalationNotifier>>());
    }

    [TearDown]
    public void ResetConfiguredTexts() => EscalationHandoffTexts.Reset();

    private void UseLanguage(string? language) =>
        _settingsReader.GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(language == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { [SettingKeys.DefaultLanguage] = language });

    private static string ApiRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null
               && !Directory.Exists(Path.Combine(directory.FullName, ApiDirectory, PluginsDirectory, LanguagesDirectory)))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull();
        return Path.Combine(directory.FullName, ApiDirectory);
    }

    private static void LoadThePacks()
    {
        var failures = new List<string>();
        AssistantTextsPluginLoader.Load(ApiRoot(), (file, ex) => failures.Add($"{file}: {ex.Message}"));
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
    }

    private static string PackSentence(string pack, string key)
    {
        var file = Path.Combine(ApiRoot(), PluginsDirectory, LanguagesDirectory, pack, AssistantTextsFile);
        return JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(file))![key];
    }

    private static EscalationChain AbsenceChain() => new()
    {
        Id = Guid.NewGuid(),
        Purpose = EscalationChainPurpose.AbsenceCoverage,
        AbsentClientName = AbsentEmployee,
        ShiftStartUtc = ShiftStartUtc,
        DeadlineUtc = ShiftStartUtc
    };

    private static EscalationChain ApprovalChain() => new()
    {
        Id = Guid.NewGuid(),
        Purpose = EscalationChainPurpose.ProactiveApproval,
        ConditionId = ConditionId,
        DeadlineUtc = ShiftStartUtc
    };

    private static EscalationStage Stage(string userId, string displayName) => new()
    {
        Id = Guid.NewGuid(),
        UserId = userId,
        UserDisplayName = displayName,
        Status = EscalationStageStatus.Notified,
        NotifiedAtUtc = ShiftStartUtc
    };

    private string RowOf(string userId) => _rows.Single(row => row.UserId == userId).ContentKey!;

    [TestCase("de", "Danke, du übernimmst den Dienst am 16.08.2026 von Erika Absent.", "Ann Acknowledger hat den Dienst am 16.08.2026 von Erika Absent übernommen.")]
    [TestCase("en", "Thanks, you're now covering the 16.08.2026 shift for Erika Absent.", "Ann Acknowledger has taken over the 16.08.2026 shift for Erika Absent.")]
    [TestCase("fr", "Merci, tu reprends le service du 16.08.2026 pour Erika Absent.", "Ann Acknowledger a repris le service du 16.08.2026 pour Erika Absent.")]
    [TestCase("it", "Grazie, ora copri il turno del 16.08.2026 per Erika Absent.", "Ann Acknowledger ha rilevato il turno del 16.08.2026 per Erika Absent.")]
    public async Task NotifyHandoffAsync_AbsenceChain_EachCoreLanguageWritesItsOwnSentences(
        string language, string confirmation, string quietNote)
    {
        LoadThePacks();
        UseLanguage(language);
        var acknowledged = Stage(AcknowledgingUserId, AcknowledgingName);

        await _sut.NotifyHandoffAsync(AbsenceChain(), acknowledged, [acknowledged, Stage(EarlierUserId, "Earlier Planner")]);

        RowOf(AcknowledgingUserId).ShouldBe(confirmation);
        RowOf(EarlierUserId).ShouldBe(quietNote);
    }

    [TestCase(Japanese, Japanese)]
    [TestCase(ChineseSimplified, ChineseSimplified)]
    [TestCase("zh-cn", ChineseSimplified)]
    [TestCase("ZH-CN", ChineseSimplified)]
    [TestCase("ja-JP", Japanese)]
    public async Task NotifyHandoffAsync_AbsenceChain_APackLanguageWritesTheSentencesOfItsPack(string configured, string pack)
    {
        LoadThePacks();
        UseLanguage(configured);
        var acknowledged = Stage(AcknowledgingUserId, AcknowledgingName);

        await _sut.NotifyHandoffAsync(AbsenceChain(), acknowledged, [acknowledged, Stage(EarlierUserId, "Earlier Planner")]);

        RowOf(AcknowledgingUserId).ShouldBe(
            PackSentence(pack, EscalationHandoffTexts.AcknowledgedConfirmation)
                .Replace("{{date}}", ShiftDate).Replace("{{employee}}", AbsentEmployee));
        RowOf(EarlierUserId).ShouldBe(
            PackSentence(pack, EscalationHandoffTexts.HandoffQuietNote)
                .Replace("{{responder}}", AcknowledgingName).Replace("{{date}}", ShiftDate).Replace("{{employee}}", AbsentEmployee));
        RowOf(AcknowledgingUserId).ShouldNotContain("{{");
        RowOf(EarlierUserId).ShouldNotContain("{{");
    }

    [TestCase(null)]
    [TestCase("kl")]
    [TestCase("")]
    public async Task NotifyHandoffAsync_UnknownOrUnsetLanguage_WritesEnglish(string? language)
    {
        LoadThePacks();
        UseLanguage(language);
        var acknowledged = Stage(AcknowledgingUserId, AcknowledgingName);

        await _sut.NotifyHandoffAsync(AbsenceChain(), acknowledged, [acknowledged]);

        RowOf(AcknowledgingUserId).ShouldBe("Thanks, you're now covering the 16.08.2026 shift for Erika Absent.");
    }

    [Test]
    public async Task NotifyHandoffAsync_APackLanguageThatWasNeverLoaded_WritesEnglish()
    {
        UseLanguage(Japanese);
        var acknowledged = Stage(AcknowledgingUserId, AcknowledgingName);

        await _sut.NotifyHandoffAsync(AbsenceChain(), acknowledged, [acknowledged]);

        RowOf(AcknowledgingUserId).ShouldStartWith("Thanks, you're now covering");
    }

    [Test]
    public async Task NotifyHandoffAsync_AbsenceChain_ConfirmationAlsoGoesOverTheMessenger_TheQuietNoteDoesNot()
    {
        LoadThePacks();
        UseLanguage(Japanese);
        var acknowledged = Stage(AcknowledgingUserId, AcknowledgingName);

        await _sut.NotifyHandoffAsync(AbsenceChain(), acknowledged, [acknowledged, Stage(EarlierUserId, "Earlier Planner")]);

        await _messenger.Received(1).TrySendAsync(
            AcknowledgingUserId, RowOf(AcknowledgingUserId), AgentTriggerKinds.EscalationStageAlert, Arg.Any<CancellationToken>());
        await _messenger.DidNotReceive().TrySendAsync(
            EarlierUserId, Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>());
    }

    [TestCase("de", "Danke, du hast die Massnahme unknown action zur Feststellung empty_container freigegeben.")]
    [TestCase("en", "Thanks, you approved the action unknown action for the finding empty_container.")]
    [TestCase("fr", "Merci, tu as approuvé l'action unknown action pour le constat empty_container.")]
    [TestCase("it", "Grazie, hai approvato l'azione unknown action per la rilevazione empty_container.")]
    public async Task NotifyHandoffAsync_ApprovalChain_EachCoreLanguageWritesItsConfirmation_AndNoMessengerSend(
        string language, string confirmationStart)
    {
        LoadThePacks();
        UseLanguage(language);
        var acknowledged = Stage(AcknowledgingUserId, AcknowledgingName);

        await _sut.NotifyHandoffAsync(ApprovalChain(), acknowledged, [acknowledged]);

        RowOf(AcknowledgingUserId).ShouldStartWith(confirmationStart);
        await _messenger.DidNotReceiveWithAnyArgs().TrySendAsync(default!, default!, default!, default);
    }

    [Test]
    public async Task NotifyHandoffAsync_ApprovalChain_APackLanguageWritesTheQuietNoteOfItsPack()
    {
        LoadThePacks();
        UseLanguage(Japanese);
        var acknowledged = Stage(AcknowledgingUserId, AcknowledgingName);

        await _sut.NotifyHandoffAsync(ApprovalChain(), acknowledged, [acknowledged, Stage(EarlierUserId, "Earlier Planner")]);

        RowOf(EarlierUserId).ShouldBe(
            PackSentence(Japanese, EscalationHandoffTexts.ApprovalHandoffQuietNote)
                .Replace("{{responder}}", AcknowledgingName)
                .Replace("{{action}}", UnknownAction)
                .Replace("{{finding}}", TriggerKind));
    }

    [Test]
    public async Task NotifyHandoffAsync_AValueThatLooksLikeAPlaceholder_IsNotExpandedTwice()
    {
        UseLanguage("en");
        _conditionRepository.GetByIdAsync(ConditionId, Arg.Any<CancellationToken>())
            .Returns(new AgentCondition { Id = ConditionId, TriggerKind = "kind{{action}}" });
        var acknowledged = Stage(AcknowledgingUserId, AcknowledgingName);

        await _sut.NotifyHandoffAsync(ApprovalChain(), acknowledged, [acknowledged]);

        RowOf(AcknowledgingUserId).ShouldContain("for the finding kind{{action}}.");
    }

    [TestCase("de", "Niemand hat die Massnahme unknown action zur Feststellung empty_container freigegeben; die Frist ist abgelaufen.")]
    [TestCase("en", "Nobody approved the action unknown action for the finding empty_container; the window has lapsed.")]
    [TestCase("fr", "Personne n'a approuvé l'action unknown action pour le constat empty_container ; le délai est écoulé.")]
    [TestCase("it", "Nessuno ha approvato l'azione unknown action per la rilevazione empty_container; il termine è scaduto.")]
    public async Task NotifyExhaustedAsync_ApprovalChain_EachCoreLanguageWritesItsNote(string language, string noteStart)
    {
        LoadThePacks();
        UseLanguage(language);

        await _sut.NotifyExhaustedAsync(ApprovalChain(), [Stage(EarlierUserId, "Earlier Planner")]);

        RowOf(EarlierUserId).ShouldStartWith(noteStart);
    }

    [Test]
    public async Task NotifyExhaustedAsync_ApprovalChain_APackLanguageWritesTheNoteOfItsPack()
    {
        LoadThePacks();
        UseLanguage(ChineseSimplified);

        await _sut.NotifyExhaustedAsync(ApprovalChain(), [Stage(EarlierUserId, "Earlier Planner")]);

        RowOf(EarlierUserId).ShouldBe(
            PackSentence(ChineseSimplified, EscalationHandoffTexts.ApprovalExhaustedNote)
                .Replace("{{action}}", UnknownAction)
                .Replace("{{finding}}", TriggerKind));
    }
}
