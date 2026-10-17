// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The explain chip's label arrives as the user's message while a confirmation is held (that is the situation the chip is
/// offered in). TurnPreparationService must never read it as the answer to the held question: in every one of the 25
/// languages, with the vocabularies of all shipped language packs loaded, the turn is neither forced into
/// confirm_pending_action nor given the "ask yes or no" note of an ambiguous reply. A plain "Ja" in the same setup does force
/// it, which shows the held token and the confirm function are really in reach.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Klacksy;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Skills;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class ExplainOfferChipPendingConfirmationTests
{
    private const string PluginsDirectory = "Plugins";
    private const string LanguagesDirectory = "Languages";
    private const string HeldToken = "held-token";
    private const string HeldSkill = "bulk_delete_works";

    private static readonly Guid UserId = Guid.NewGuid();

    private TurnPreparationService _service = null!;

    [SetUp]
    public void SetUp()
    {
        AssistantTextCatalogues.ResetAll();
        var failures = new List<string>();
        AssistantTextsPluginLoader.Load(RepositoryRootLocator.ApiProject, (file, ex) => failures.Add($"{file}: {ex.Message}"));
        failures.ShouldBeEmpty(string.Join(Environment.NewLine, failures));
        ShippedConversationSignals.Install();

        var store = Substitute.For<IPendingConfirmationStore>();
        store.PeekLatestForUser(Arg.Any<Guid>(), Arg.Any<TimeSpan>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(new PendingConfirmationHandle(HeldToken, HeldSkill));

        _service = new TurnPreparationService(
            store,
            new RecipeEngineService(
                Substitute.For<IServiceScopeFactory>(), Substitute.For<IPendingRecipeStore>(),
                Substitute.For<ILogger<RecipeEngineService>>()),
            Substitute.For<IRecipeRunRecorder>(),
            new RecipeSlotExtractor(Substitute.For<ILogger<RecipeSlotExtractor>>()),
            Substitute.For<IAssistantLastActionStore>(),
            Substitute.For<IDeterministicRouteProbe>(),
            Substitute.For<ISkillInverseResolver>(),
            Substitute.For<ILogger<TurnPreparationService>>());
    }

    [TearDown]
    public void TearDown()
    {
        ShippedConversationSignals.Uninstall();
        AssistantTextCatalogues.ResetAll();
    }

    private static IReadOnlyList<string> AllLanguages() =>
        ExplainOfferTexts.CoreLanguages
            .Concat(Directory.GetDirectories(Path.Combine(RepositoryRootLocator.ApiProject, PluginsDirectory, LanguagesDirectory))
                .Where(dir => File.Exists(Path.Combine(dir, LanguagePluginConstants.ManifestFileName)))
                .Select(dir => Path.GetFileName(dir))
                .Where(code => !LanguagePluginConstants.CoreLanguages.Contains(code)))
            .ToList();

    private static LLMContext Context(string message, string language) => new()
    {
        UserId = UserId.ToString(),
        ConversationId = "conv-chip",
        Message = message,
        Language = language,
        AvailableFunctions = [new LLMFunction { Name = AutonomyDefaults.ConfirmPendingActionSkillName }]
    };

    [Test]
    public void APlainYes_ForcesTheHeldConfirmation_SoTheSetupIsLive()
    {
        var (force, confirmFunction, note) = _service.ResolvePendingConfirmation(Context("Ja", "de"));

        force.ShouldBeTrue();
        confirmFunction.ShouldNotBeNull();
        note.ShouldNotBeNull();
    }

    [Test]
    public void TheChipLabel_NeverRedeemsOrQuestionsTheHeldConfirmation_InAnyLanguage()
    {
        var languages = AllLanguages();
        languages.Count.ShouldBe(25);
        var problems = new List<string>();

        foreach (var language in languages)
        {
            ExplainOfferTexts.TryGetOwnText(ExplainOfferTexts.ChipLabel, language, out var label).ShouldBeTrue(language);

            var (force, confirmFunction, note) = _service.ResolvePendingConfirmation(Context(label, language));

            if (force || confirmFunction != null || note != null)
            {
                problems.Add($"{language} '{label}': force={force}, confirm={confirmFunction?.Name}, note={note != null}");
            }
        }

        problems.ShouldBeEmpty(string.Join(Environment.NewLine, problems));
    }
}
