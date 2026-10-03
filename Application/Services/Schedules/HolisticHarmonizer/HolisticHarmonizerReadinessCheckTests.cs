// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using SettingKeys = Klacks.Api.Application.Constants.Settings;
using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer;
using Klacks.Api.Domain.Interfaces.Settings;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Application.Services.Schedules.HolisticHarmonizer;

/// <summary>
/// The AutoWizard chain asks this check before and after its third stage. In the default deterministic mode
/// stage 3 needs no model and is always ready. In LLM mode a missing model or a model that already failed the
/// image round-trip must say "not ready" so the chain completes with the Harmonizer result instead of failing;
/// an unmeasured model must say "ready" because the run measures it itself.
/// </summary>
[TestFixture]
public sealed class HolisticHarmonizerReadinessCheckTests
{
    private const string ModelId = "deepseek-v4-pro";

    private ISettingsReader _settingsReader = null!;
    private HolisticHarmonizerModelCapabilityCache _capabilityCache = null!;
    private HolisticHarmonizerReadinessCheck _sut = null!;

    [SetUp]
    public void Setup()
    {
        _settingsReader = Substitute.For<ISettingsReader>();
        _capabilityCache = new HolisticHarmonizerModelCapabilityCache();
        _sut = new HolisticHarmonizerReadinessCheck(_settingsReader, _capabilityCache);
        ConfigureMode(HolisticHarmonizerModes.Llm);
    }

    private void ConfigureMode(string? mode)
    {
        _settingsReader.GetSetting(SettingKeys.HOLISTIC_HARMONIZER_MODE)
            .Returns(mode == null ? null : new SettingsEntity { Type = SettingKeys.HOLISTIC_HARMONIZER_MODE, Value = mode });
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase(HolisticHarmonizerModes.Deterministic)]
    [TestCase("unknown-mode")]
    public async Task DeterministicMode_WithoutModel_IsReady(string? mode)
    {
        ConfigureMode(mode);
        ConfigureModel(null);

        var readiness = await _sut.CheckAsync();

        readiness.IsReady.ShouldBeTrue();
        readiness.Reason.ShouldBeNull();
    }

    [Test]
    public async Task DeterministicMode_IgnoresTextOnlyVerdictOfConfiguredModel()
    {
        ConfigureMode(HolisticHarmonizerModes.Deterministic);
        ConfigureModel(ModelId);
        _capabilityCache.Store(ModelId, isVisionCapable: false, error: "answered without reading the image");

        (await _sut.CheckAsync()).IsReady.ShouldBeTrue();
    }

    [Test]
    public async Task LlmModeIsCaseInsensitive()
    {
        ConfigureMode("LLM");
        ConfigureModel(null);

        (await _sut.CheckAsync()).IsReady.ShouldBeFalse();
    }

    private void ConfigureModel(string? modelId)
    {
        _settingsReader.GetSetting(SettingKeys.HOLISTIC_HARMONIZER_LLM_MODEL)
            .Returns(modelId == null ? null : new SettingsEntity { Type = SettingKeys.HOLISTIC_HARMONIZER_LLM_MODEL, Value = modelId });
    }

    [Test]
    public async Task NoModelConfigured_IsNotReady()
    {
        ConfigureModel(null);

        var readiness = await _sut.CheckAsync();

        readiness.IsReady.ShouldBeFalse();
        readiness.Reason.ShouldBe(HolisticHarmonizerReadinessCheck.ModelNotConfiguredReason);
    }

    [Test]
    public async Task BlankModelSetting_IsNotReady()
    {
        ConfigureModel("  ");

        (await _sut.CheckAsync()).IsReady.ShouldBeFalse();
    }

    [Test]
    public async Task ModelKnownToBeTextOnly_IsNotReady()
    {
        ConfigureModel(ModelId);
        _capabilityCache.Store(ModelId, isVisionCapable: false, error: "answered without reading the image");

        var readiness = await _sut.CheckAsync();

        readiness.IsReady.ShouldBeFalse();
        readiness.Reason!.ShouldContain(ModelId);
        readiness.Reason.ShouldContain("answered without reading the image");
    }

    [Test]
    public async Task ModelNeverMeasured_IsReady()
    {
        ConfigureModel(ModelId);

        var readiness = await _sut.CheckAsync();

        readiness.IsReady.ShouldBeTrue();
        readiness.Reason.ShouldBeNull();
    }

    [Test]
    public async Task ModelKnownToBeVisionCapable_IsReady()
    {
        ConfigureModel(ModelId);
        _capabilityCache.Store(ModelId, isVisionCapable: true, error: null);

        (await _sut.CheckAsync()).IsReady.ShouldBeTrue();
    }
}
