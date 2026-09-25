// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for EscalationHandoffTextService: the sentence follows the installation language (English by
/// default and for an unknown tag, a pack language once its pack was loaded, a regional tag reaches its
/// language), values are inserted in one pass so a value that looks like a placeholder is never expanded a
/// second time, a missing value leaves its placeholder standing, a loaded pack that lacks the key falls back
/// to English with a warning instead of dropping the note, and a cancelled language lookup is not swallowed.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Infrastructure.Services.Assistant.Escalation;
using Klacks.Api.Infrastructure.Services.Settings;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant.Escalation;

[TestFixture]
public class EscalationHandoffTextServiceTests
{
    private const string PackLanguage = "xx-Pack";
    private const string PackSentence = "PACK {{responder}} / {{date}}";
    private const string EmployeeParameter = "employee";

    private ISettingsReader _settingsReader = null!;
    private RecordingLogger<EscalationHandoffTextService> _logger = null!;
    private EscalationHandoffTextService _service = null!;

    [SetUp]
    public void SetUp()
    {
        EscalationHandoffTexts.Reset();
        _settingsReader = Substitute.For<ISettingsReader>();
        UseLanguage(null);
        _logger = new RecordingLogger<EscalationHandoffTextService>();
        _service = new EscalationHandoffTextService(
            new InstallationLanguageResolver(_settingsReader, Substitute.For<ILogger<InstallationLanguageResolver>>()), _logger);
    }

    [TearDown]
    public void ResetConfiguredTexts() => EscalationHandoffTexts.Reset();

    private void UseLanguage(string? language) =>
        _settingsReader.GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(language == null
                ? new Dictionary<string, string>()
                : new Dictionary<string, string> { [SettingKeys.DefaultLanguage] = language });

    private static Dictionary<string, string> AbsenceValues() => new()
    {
        [EscalationHandoffPlaceholders.Date] = "16.08.2026",
        [EscalationHandoffPlaceholders.Employee] = "Erika"
    };

    [Test]
    public async Task Render_WithoutALanguageSetting_IsEnglish()
    {
        var text = await _service.RenderAsync(EscalationHandoffTexts.AcknowledgedConfirmation, AbsenceValues());

        text.ShouldBe("Thanks, you're now covering the 16.08.2026 shift for Erika.");
    }

    [Test]
    public async Task Render_GermanInstallation_IsGerman()
    {
        UseLanguage("de");

        var text = await _service.RenderAsync(EscalationHandoffTexts.AcknowledgedConfirmation, AbsenceValues());

        text.ShouldBe("Danke, du übernimmst den Dienst am 16.08.2026 von Erika.");
    }

    [Test]
    public async Task Render_ARegionalTagOfACoreLanguage_ReachesTheCoreLanguage()
    {
        UseLanguage("fr-CH");

        var text = await _service.RenderAsync(EscalationHandoffTexts.AcknowledgedConfirmation, AbsenceValues());

        text.ShouldBe("Merci, tu reprends le service du 16.08.2026 pour Erika.");
    }

    [Test]
    public async Task Render_APackLanguage_UsesTheLoadedPack()
    {
        EscalationHandoffTexts.Configure(
            PackLanguage, new Dictionary<string, string> { [EscalationHandoffTexts.HandoffQuietNote] = PackSentence });
        UseLanguage(PackLanguage);

        var text = await _service.RenderAsync(
            EscalationHandoffTexts.HandoffQuietNote,
            new Dictionary<string, string>
            {
                [EscalationHandoffPlaceholders.Responder] = "Ann",
                [EscalationHandoffPlaceholders.Date] = "16.08.2026"
            });

        text.ShouldBe("PACK Ann / 16.08.2026");
        _logger.Entries.ShouldBeEmpty();
    }

    [Test]
    public async Task Render_ALoadedPackThatLacksTheKey_FallsBackToEnglishWithAWarning()
    {
        EscalationHandoffTexts.Configure(
            PackLanguage, new Dictionary<string, string> { [EscalationHandoffTexts.HandoffQuietNote] = PackSentence });
        UseLanguage(PackLanguage);

        var text = await _service.RenderAsync(EscalationHandoffTexts.AcknowledgedConfirmation, AbsenceValues());

        text.ShouldBe("Thanks, you're now covering the 16.08.2026 shift for Erika.");
        _logger.Entries.ShouldContain(entry => entry.Level == LogLevel.Warning
            && entry.Message.Contains(EscalationHandoffTexts.AcknowledgedConfirmation, StringComparison.Ordinal)
            && entry.Message.Contains(PackLanguage, StringComparison.Ordinal));
    }

    [Test]
    public async Task Render_APackThatWasNeverLoaded_IsEnglishWithoutAWarning()
    {
        UseLanguage(PackLanguage);

        var text = await _service.RenderAsync(EscalationHandoffTexts.AcknowledgedConfirmation, AbsenceValues());

        text.ShouldStartWith("Thanks, you're now covering");
        _logger.Entries.ShouldBeEmpty();
    }

    [Test]
    public async Task Render_AValueThatLooksLikeAPlaceholder_IsNotExpandedTwice()
    {
        var values = AbsenceValues();
        values[EscalationHandoffPlaceholders.Date] = "{{employee}}";

        var text = await _service.RenderAsync(EscalationHandoffTexts.AcknowledgedConfirmation, values);

        text.ShouldBe("Thanks, you're now covering the {{employee}} shift for Erika.");
    }

    [Test]
    public async Task Render_AMissingValue_LeavesItsPlaceholderStanding()
    {
        var text = await _service.RenderAsync(
            EscalationHandoffTexts.AcknowledgedConfirmation,
            new Dictionary<string, string> { [EmployeeParameter] = "Erika" });

        text.ShouldBe("Thanks, you're now covering the {{date}} shift for Erika.");
    }

    [Test]
    public async Task Render_ACancelledLookup_IsNotSwallowed()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        _settingsReader.GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns<IReadOnlyDictionary<string, string>>(_ => throw new OperationCanceledException(cancellation.Token));

        await Should.ThrowAsync<OperationCanceledException>(
            () => _service.RenderAsync(EscalationHandoffTexts.AcknowledgedConfirmation, AbsenceValues(), cancellation.Token));
    }
}
