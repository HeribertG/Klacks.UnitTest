// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Interfaces.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer.Critic;
using Klacks.Api.Domain.Interfaces.Settings;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Shouldly;
using SettingKeys = Klacks.Api.Application.Constants.Settings;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Services.Schedules.HolisticHarmonizer.Critic;

/// <summary>
/// The critic runs at the end of every Wizard 3 run, so it must never send a plan to a model nobody chose, never leak
/// names, and never turn a provider problem into a failed run.
/// </summary>
[TestFixture]
public sealed class PlanCriticServiceTests
{
    private const string CriticModel = "critic-model";
    private const string LlmModel = "gemini-25-flash";

    private ISettingsReader _settingsReader = null!;
    private IPlanCriticClient _client = null!;
    private HolisticHarmonizerModelCapabilityCache _capabilityCache = null!;
    private PlanCriticService _sut = null!;

    [SetUp]
    public void Setup()
    {
        _settingsReader = Substitute.For<ISettingsReader>();
        _settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);
        _client = Substitute.For<IPlanCriticClient>();
        _client.ReviewAsync(Arg.Any<PlanCriticRequest>(), Arg.Any<CancellationToken>())
            .Returns(new PlanCriticClientResponse(
                [new PlanCriticFinding("night_then_early", "r00 ...", PlanCriticSeverity.High,
                    [new PlanCriticCell(0, 0, "N"), new PlanCriticCell(0, 1, "E")]),
                 new PlanCriticFinding("invented", "r01 ...", PlanCriticSeverity.Low, [new PlanCriticCell(1, 0, "L")])],
                null));
        _capabilityCache = new HolisticHarmonizerModelCapabilityCache();
        _sut = new PlanCriticService(_client, _settingsReader, _capabilityCache, NullLogger<PlanCriticService>.Instance);
    }

    private void SetSetting(string key, string value)
        => _settingsReader.GetSetting(key).Returns(new SettingsEntity { Type = key, Value = value });

    private Task<PlanCriticOutcome> Review(string? language = null)
        => _sut.ReviewAsync(PlanCriticTestPlans.TwoAgentsThreeDays(), language, CancellationToken.None);

    [Test]
    public async Task NoModelConfigured_IsSkippedWithoutCall()
    {
        var outcome = await Review();

        outcome.Status.ShouldBe(PlanCriticStatus.Skipped);
        await _client.DidNotReceiveWithAnyArgs().ReviewAsync(default!, default);
    }

    [Test]
    public async Task SeededLlmModelInDeterministicMode_IsNotUsed()
    {
        SetSetting(SettingKeys.HOLISTIC_HARMONIZER_LLM_MODEL, LlmModel);

        var outcome = await Review();

        outcome.Status.ShouldBe(PlanCriticStatus.Skipped);
        await _client.DidNotReceiveWithAnyArgs().ReviewAsync(default!, default);
    }

    [Test]
    public async Task LlmMode_FallsBackToTheWizard3Model()
    {
        SetSetting(SettingKeys.HOLISTIC_HARMONIZER_MODE, HolisticHarmonizerModes.Llm);
        SetSetting(SettingKeys.HOLISTIC_HARMONIZER_LLM_MODEL, LlmModel);

        var outcome = await Review();

        outcome.Status.ShouldBe(PlanCriticStatus.Completed);
        outcome.ModelId.ShouldBe(LlmModel);
    }

    [Test]
    public async Task CriticModel_WinsOverTheWizard3Model()
    {
        SetSetting(SettingKeys.HOLISTIC_HARMONIZER_CRITIC_MODEL, " " + CriticModel + " ");
        SetSetting(SettingKeys.HOLISTIC_HARMONIZER_MODE, HolisticHarmonizerModes.Llm);
        SetSetting(SettingKeys.HOLISTIC_HARMONIZER_LLM_MODEL, LlmModel);

        var outcome = await Review();

        outcome.ModelId.ShouldBe(CriticModel);
    }

    [Test]
    public async Task OnlyVerifiedFindings_AreReturned()
    {
        SetSetting(SettingKeys.HOLISTIC_HARMONIZER_CRITIC_MODEL, CriticModel);

        var outcome = await Review();

        outcome.Findings.ShouldHaveSingleItem().Pattern.ShouldBe("night_then_early");
        outcome.DiscardedFindingCount.ShouldBe(1);
    }

    [Test]
    public async Task Request_CarriesNoAgentNamesAndDefaultsToEnglish()
    {
        SetSetting(SettingKeys.HOLISTIC_HARMONIZER_CRITIC_MODEL, CriticModel);

        await Review();

        await _client.Received(1).ReviewAsync(
            Arg.Is<PlanCriticRequest>(r =>
                r.Language == "en"
                && r.PlanPng.Length > 0
                && !r.PlanText.Contains(PlanCriticTestPlans.FirstAgentName)
                && !r.PlanText.Contains(PlanCriticTestPlans.SecondAgentName)
                && r.PlanText.Contains("r00")),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ModelKnownToBeTextOnly_IsSkipped()
    {
        SetSetting(SettingKeys.HOLISTIC_HARMONIZER_CRITIC_MODEL, CriticModel);
        _capabilityCache.Store(CriticModel, false, "text only");

        var outcome = await Review();

        outcome.Status.ShouldBe(PlanCriticStatus.Skipped);
        await _client.DidNotReceiveWithAnyArgs().ReviewAsync(default!, default);
    }

    [Test]
    public async Task ClientError_IsReportedAsFailed()
    {
        SetSetting(SettingKeys.HOLISTIC_HARMONIZER_CRITIC_MODEL, CriticModel);
        _client.ReviewAsync(Arg.Any<PlanCriticRequest>(), Arg.Any<CancellationToken>())
            .Returns(PlanCriticClientResponse.Failure("provider down"));

        var outcome = await Review();

        outcome.Status.ShouldBe(PlanCriticStatus.Failed);
        outcome.Message.ShouldBe("provider down");
    }

    [Test]
    public async Task ClientThrowingOrCancelled_IsReportedAsFailedNotThrown()
    {
        SetSetting(SettingKeys.HOLISTIC_HARMONIZER_CRITIC_MODEL, CriticModel);
        _client.ReviewAsync(Arg.Any<PlanCriticRequest>(), Arg.Any<CancellationToken>())
            .Throws(new OperationCanceledException());

        var outcome = await Review();

        outcome.Status.ShouldBe(PlanCriticStatus.Failed);
    }
}
