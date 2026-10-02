// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the one place server-created scenario names are built: the prefix is in the planner's language when one
/// is passed and claimed (a regional tag reaching its base language), otherwise in the installation language and
/// never silently in English; the period is written as culture-neutral ISO calendar dates whatever the process
/// culture; a single day is written once; and a name the group already uses gets the next free counter suffix.
/// </summary>

using System.Globalization;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class ScenarioNameGeneratorTests
{
    private const string German = "de";
    private const string SwissGerman = "de-CH";
    private const string French = "fr";
    private const string Italian = "it";
    private const string UnknownLanguage = "xx-XX";
    private const string PackLanguage = "qa";
    private const string PackLanguageRegional = "qa-QA";
    private const string PackProposal = "Qa-Proposal";
    private const string IncompletePackLanguage = "qb";
    private const string ThaiCulture = "th-TH";
    private const string SaudiArabicCulture = "ar-SA";

    private static readonly DateOnly From = new(2026, 3, 2);
    private static readonly DateOnly Until = new(2026, 3, 8);
    private static readonly Guid GroupId = Guid.NewGuid();

    private IAnalyseScenarioRepository _repository = null!;
    private IInstallationLanguageResolver _installationLanguage = null!;
    private ScenarioNameGenerator _generator = null!;

    [SetUp]
    public void SetUp()
    {
        AssistantTextCatalogues.ResetAll();
        _repository = Substitute.For<IAnalyseScenarioRepository>();
        _repository.GetByGroupAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(new List<AnalyseScenario>());
        _installationLanguage = Substitute.For<IInstallationLanguageResolver>();
        _installationLanguage.ResolveAsync(Arg.Any<CancellationToken>()).Returns(French);
        _generator = new ScenarioNameGenerator(_repository, _installationLanguage, NullLogger<ScenarioNameGenerator>.Instance);
    }

    [TearDown]
    public void TearDown() => AssistantTextCatalogues.ResetAll();

    private Task<string> Generate(ScenarioNameKind kind, string? language, DateOnly? until = null) =>
        _generator.GenerateAsync(kind, From, until ?? Until, GroupId, language, CancellationToken.None);

    private void ExistingNames(params string[] names) =>
        _repository.GetByGroupAsync(GroupId, Arg.Any<CancellationToken>())
            .Returns(names.Select(name => new AnalyseScenario { Name = name }).ToList());

    [Test]
    public async Task APassedCoreLanguage_NamesThePeriodWithIsoDates()
    {
        var name = await Generate(ScenarioNameKind.Proposal, German);

        name.ShouldBe("Vorschlag 2026-03-02 – 2026-03-08");
        await _installationLanguage.DidNotReceive().ResolveAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ASingleDay_IsWrittenOnce()
    {
        var name = await Generate(ScenarioNameKind.AbsenceCover, Italian, until: From);

        name.ShouldBe("Sostituzione per assenza 2026-03-02");
    }

    [Test]
    public async Task ARegionalTag_ReachesItsBaseLanguage()
    {
        var name = await Generate(ScenarioNameKind.AutoPlan, SwissGerman);

        name.ShouldBe("Auto-Erstellung Plan 2026-03-02 – 2026-03-08");
    }

    [Test]
    public async Task AnUnknownLanguage_FallsBackToTheInstallationLanguage_NotToEnglish()
    {
        var name = await Generate(ScenarioNameKind.Proposal, UnknownLanguage);

        name.ShouldBe("Proposition 2026-03-02 – 2026-03-08");
    }

    [Test]
    public async Task NoLanguage_UsesTheInstallationLanguage()
    {
        var name = await Generate(ScenarioNameKind.Optimizer, null);

        name.ShouldBe("Optimisé 2026-03-02 – 2026-03-08");
    }

    [Test]
    public async Task AConfiguredPackLanguage_IsUsed_AlsoThroughItsRegionalTag()
    {
        ScenarioNameTexts.Configure(PackLanguage, new Dictionary<string, string> { [ScenarioNameTexts.Proposal] = PackProposal });

        (await Generate(ScenarioNameKind.Proposal, PackLanguage)).ShouldBe($"{PackProposal} 2026-03-02 – 2026-03-08");
        (await Generate(ScenarioNameKind.Proposal, PackLanguageRegional)).ShouldBe($"{PackProposal} 2026-03-02 – 2026-03-08");
    }

    [Test]
    public async Task APackThatLacksTheKey_FallsBackToTheInstallationLanguage()
    {
        ScenarioNameTexts.Configure(PackLanguage, new Dictionary<string, string> { [ScenarioNameTexts.Proposal] = PackProposal });

        var name = await Generate(ScenarioNameKind.Llm, PackLanguage);

        name.ShouldBe("Plan IA 2026-03-02 – 2026-03-08");
    }

    [Test]
    public async Task AnInstallationPackThatLacksTheKey_GoesOutInEnglish_AsTheLastFloor()
    {
        ScenarioNameTexts.Configure(IncompletePackLanguage, new Dictionary<string, string> { [ScenarioNameTexts.Proposal] = PackProposal });
        _installationLanguage.ResolveAsync(Arg.Any<CancellationToken>()).Returns(IncompletePackLanguage);

        var name = await Generate(ScenarioNameKind.Harmonized, null);

        name.ShouldBe("Harmonized 2026-03-02 – 2026-03-08");
    }

    [Test]
    public async Task ATakenName_GetsTheNextFreeCounter()
    {
        ExistingNames("Vorschlag 2026-03-02 – 2026-03-08", "Vorschlag 2026-03-02 – 2026-03-08 (2)");

        var name = await Generate(ScenarioNameKind.Proposal, German);

        name.ShouldBe("Vorschlag 2026-03-02 – 2026-03-08 (3)");
        await _repository.Received(1).GetByGroupAsync(GroupId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TheSameNameInAnotherLanguage_DoesNotCollide()
    {
        ExistingNames("Proposal 2026-03-02 – 2026-03-08");

        var name = await Generate(ScenarioNameKind.Proposal, German);

        name.ShouldBe("Vorschlag 2026-03-02 – 2026-03-08");
    }

    [TestCase(ThaiCulture)]
    [TestCase(SaudiArabicCulture)]
    public async Task TheDates_DoNotDependOnTheProcessCulture(string cultureName)
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo(cultureName);

            var name = await Generate(ScenarioNameKind.Plan, German);

            name.ShouldBe("Plan 2026-03-02 – 2026-03-08");
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Test]
    public async Task EveryKind_HasAPrefixInEveryCoreLanguage()
    {
        foreach (var kind in Enum.GetValues<ScenarioNameKind>())
        {
            foreach (var language in ScenarioNameTexts.CoreLanguages)
            {
                var name = await Generate(kind, language);
                name.ShouldEndWith(" 2026-03-02 – 2026-03-08", customMessage: $"{kind}/{language}");
                name.Length.ShouldBeGreaterThan(" 2026-03-02 – 2026-03-08".Length, $"{kind}/{language}");
            }
        }
    }
}
