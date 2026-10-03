// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Llm;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Mutations;
using Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Search;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingKeys = Klacks.Api.Application.Constants.Settings;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Services.Schedules.HolisticHarmonizer;

/// <summary>
/// Stage 3 must run without any LLM or API key by default (on-prem without internet). Only the explicit
/// "llm" mode touches the proposal provider and requires a configured model.
/// </summary>
[TestFixture]
public sealed class HolisticHarmonizerRunServiceModeTests
{
    private const int FixtureSeed = 11;
    private static readonly DateOnly PeriodFrom = new(2026, 3, 2);
    private static readonly DateOnly PeriodUntil = new(2026, 3, 22);

    private ISettingsReader _settingsReader = null!;
    private IPlanProposalProvider _provider = null!;
    private HarmonizerResultCache _resultCache = null!;
    private HolisticHarmonizerRunService _sut = null!;

    [SetUp]
    public void Setup()
    {
        var contextBuilder = Substitute.For<IHarmonizerContextBuilder>();
        contextBuilder
            .BuildContextAsync(Arg.Any<HarmonizerContextRequest>(), Arg.Any<CancellationToken>())
            .Returns(_ => DeterministicSearchFixture.BuildInput(FixtureSeed));

        _settingsReader = Substitute.For<ISettingsReader>();
        _settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);
        _provider = Substitute.For<IPlanProposalProvider>();
        _resultCache = new HarmonizerResultCache();

        _sut = new HolisticHarmonizerRunService(
            new HolisticHarmonizerEngine(
                contextBuilder,
                _provider,
                new HolisticHarmonizerModelCapabilityCache(),
                NullLogger<HolisticHarmonizerEngine>.Instance),
            new HolisticHarmonizerDeterministicEngine(contextBuilder, NullLogger<HolisticHarmonizerDeterministicEngine>.Instance),
            _resultCache,
            _settingsReader,
            Substitute.For<IScheduleSnapshotMarkerService>(),
            NullLogger<HolisticHarmonizerRunService>.Instance);
    }

    [TestCase(null)]
    [TestCase(HolisticHarmonizerModes.Deterministic)]
    [TestCase("unknown-mode")]
    public async Task RunAsync_DeterministicModeWithoutModel_ImprovesPlanAndNeverCallsTheLlm(string? mode)
    {
        // Arrange
        ConfigureMode(mode);

        // Act
        var outcome = await _sut.RunAsync(BuildInput(), CancellationToken.None);

        // Assert
        outcome.IsSuccess.ShouldBeTrue();
        outcome.Result!.LlmModelId.ShouldBe(HolisticHarmonizerDeterministicDefaults.EngineLabel);
        outcome.Result.LlmParsingError.ShouldBeNull();
        outcome.Result.AbortedOnUnusableResponses.ShouldBeFalse();
        outcome.Result.FitnessAfter.ShouldBeGreaterThan(outcome.Result.FitnessBefore);
        outcome.Result.Iterations.ShouldNotBeEmpty();
        outcome.Result.Iterations.ShouldAllBe(i => i.Result == BatchAcceptance.Accepted);
        _resultCache.TryGet(outcome.JobId!.Value, out _, out _, out _, out _, out _, out _).ShouldBeTrue();
        await _provider.DidNotReceiveWithAnyArgs().CapabilityCheckAsync(default!, default);
        await _provider.DidNotReceiveWithAnyArgs().PingAsync(default!, default);
        await _provider.DidNotReceiveWithAnyArgs().ProposeAsync(default!, default);
        await _settingsReader.DidNotReceive().GetSetting(SettingKeys.HOLISTIC_HARMONIZER_LLM_MODEL);
    }

    [Test]
    public async Task RunAsync_LlmModeWithoutModel_FailsWithoutTouchingTheProvider()
    {
        // Arrange
        ConfigureMode(HolisticHarmonizerModes.Llm);

        // Act
        var outcome = await _sut.RunAsync(BuildInput(), CancellationToken.None);

        // Assert
        outcome.IsSuccess.ShouldBeFalse();
        outcome.FailureMessage!.ShouldContain("not configured");
        await _provider.DidNotReceiveWithAnyArgs().ProposeAsync(default!, default);
    }

    [Test]
    public void Parse_OnlyExplicitLlmSelectsTheLlmPath()
    {
        HolisticHarmonizerModes.Parse(" llm ").ShouldBe(HolisticHarmonizerMode.Llm);
        HolisticHarmonizerModes.Parse(null).ShouldBe(HolisticHarmonizerMode.Deterministic);
        HolisticHarmonizerModes.Parse("claude").ShouldBe(HolisticHarmonizerMode.Deterministic);
        HolisticHarmonizerModes.IsKnown("claude").ShouldBeFalse();
        HolisticHarmonizerModes.IsKnown(null).ShouldBeTrue();
    }

    private void ConfigureMode(string? mode)
        => _settingsReader.GetSetting(SettingKeys.HOLISTIC_HARMONIZER_MODE)
            .Returns(mode == null ? null : new SettingsEntity { Type = SettingKeys.HOLISTIC_HARMONIZER_MODE, Value = mode });

    private static HolisticHarmonizerRunInput BuildInput()
        => new(PeriodFrom, PeriodUntil, [Guid.NewGuid()], AnalyseToken: null, Language: "de");
}
