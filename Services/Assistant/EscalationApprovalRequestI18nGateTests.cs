// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guards that the approval request of a ProactiveApproval chain stage has a sentence wherever it can be
/// rendered from: the four core catalogues of Klacks.Ui and every language plugin that already carries
/// the absence stage alert. Follows EvalRegressionI18nGateTests: the Klacks.Ui files live in a different
/// repository and the backend CI job does not check them out, so that half reports itself inconclusive
/// when they are unreachable; the plugin half ships inside Klacks.Api and has no such excuse. The last
/// test closes the loop the other way: the event must supply exactly the placeholders the catalogues
/// interpolate, or a recipient reads raw braces. Deliberately NOT part of MessengerProactiveTexts - an
/// approval request never goes out over the messenger, so it must not enter that bijection.
/// </summary>

using System.Text.Json;
using System.Text.RegularExpressions;
using Klacks.Api.Application.Services.Assistant.Escalation;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Services.Assistant;

[TestFixture]
public class EscalationApprovalRequestI18nGateTests
{
    private static readonly string[] CoreLanguages = ["de", "en", "fr", "it"];

    private static readonly string[] PlaceholderNames =
    [
        EscalationApprovalRequestTriggerEvent.FindingParameter,
        EscalationApprovalRequestTriggerEvent.ActionParameter,
        EscalationApprovalRequestTriggerEvent.DueTimeParameter
    ];

    private static readonly Regex Placeholder = new(@"\{\{(\w+)\}\}", RegexOptions.Compiled);

    private const string UiCatalogueRelativePath = "Klacks.Ui/src/assets/i18n";
    private const string PluginLanguagesRelativePath = "Klacks.Api/Plugins/Languages";
    private const string TranslationsFileName = "translations.json";
    private const int ExpectedPluginCatalogues = 21;

    [Test]
    public void TheSentenceExistsInAllFourCoreCatalogues()
    {
        var catalogueDirectory = FindDirectory(UiCatalogueRelativePath);
        if (catalogueDirectory == null)
        {
            Assert.Inconclusive($"'{UiCatalogueRelativePath}' is not reachable from this working tree.");
            return;
        }

        foreach (var language in CoreLanguages)
        {
            var path = Path.Combine(catalogueDirectory, language + ".json");
            File.Exists(path).ShouldBeTrue($"Missing frontend catalogue '{path}'.");
            AssertSentence(path);
        }
    }

    [Test]
    public void EveryPluginCatalogueCarryingTheStageAlertAlsoCarriesTheApprovalRequest()
    {
        var languagesDirectory = FindDirectory(PluginLanguagesRelativePath);
        if (languagesDirectory == null)
        {
            Assert.Inconclusive($"'{PluginLanguagesRelativePath}' is not reachable from this working tree.");
            return;
        }

        var checkedCatalogues = 0;

        foreach (var directory in Directory.GetDirectories(languagesDirectory))
        {
            var path = Path.Combine(directory, TranslationsFileName);
            if (!File.Exists(path))
            {
                continue;
            }

            using var document = JsonDocument.Parse(File.ReadAllText(path));
            if (!document.RootElement.TryGetProperty(ProactiveMessageI18nKeys.EscalationStageAlert, out _))
            {
                continue;
            }

            AssertSentence(path);
            checkedCatalogues++;
        }

        checkedCatalogues.ShouldBe(
            ExpectedPluginCatalogues,
            "Another number of language plugins carries the stage alert key than this gate scans.");
    }

    [Test]
    public void TheEventSuppliesExactlyThePlaceholdersTheCataloguesInterpolate()
    {
        var triggerEvent = new EscalationApprovalRequestTriggerEvent(
            Guid.NewGuid(),
            Guid.NewGuid().ToString(),
            Guid.NewGuid(),
            AgentTriggerKinds.EmptyContainer,
            "create_container_template",
            new DateTime(2026, 9, 21, 8, 0, 0, DateTimeKind.Utc),
            TimeZoneInfo.Utc);

        triggerEvent.SummaryParams.Keys.ShouldBe(PlaceholderNames, ignoreOrder: true);
        triggerEvent.Summary.ShouldBe(ProactiveMessageMarkers.I18nPrefix + ProactiveMessageI18nKeys.EscalationApprovalRequest);
        triggerEvent.Kind.ShouldBe(AgentTriggerKinds.EscalationStageAlert);
    }

    private static void AssertSentence(string path)
    {
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        document.RootElement
            .TryGetProperty(ProactiveMessageI18nKeys.EscalationApprovalRequest, out var sentence)
            .ShouldBeTrue($"'{ProactiveMessageI18nKeys.EscalationApprovalRequest}' is missing from '{path}'.");

        var text = sentence.GetString();
        text.ShouldNotBeNullOrWhiteSpace($"'{ProactiveMessageI18nKeys.EscalationApprovalRequest}' is empty in '{path}'.");

        var used = Placeholder.Matches(text!).Select(match => match.Groups[1].Value).Distinct().ToList();
        used.ShouldBe(PlaceholderNames, ignoreOrder: true, customMessage: $"Placeholders in '{path}' do not match the event.");
    }

    private static string? FindDirectory(string relativePath)
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, relativePath.Replace('/', Path.DirectorySeparatorChar));
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        return null;
    }
}
