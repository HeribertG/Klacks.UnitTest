// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins how the learning mode and the gate's minimum net gain are read from the settings table. Every
/// installation without the key, or with a value nobody meant, must end up in Collect: that is the switch
/// that stops the loop from rewriting a live catalogue.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant.Learning;

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Learning;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using NSubstitute;
using NUnit.Framework;
using Shouldly;
using SettingsKeys = Klacks.Api.Application.Constants.Settings;
using SettingsRow = Klacks.Api.Domain.Models.Settings.Settings;

[TestFixture]
public class SkillLearningOptionsProviderTests
{
    private ISettingsRepository _settings = null!;
    private ILLMRepository _llm = null!;
    private SkillLearningOptionsProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _settings = Substitute.For<ISettingsRepository>();
        _settings.GetSettingNoTracking(Arg.Any<string>()).Returns((SettingsRow?)null);
        _llm = Substitute.For<ILLMRepository>();
        _llm.GetDefaultModelAsync().Returns((LLMModel?)null);
        _provider = new SkillLearningOptionsProvider(_settings, _llm);
    }

    private void Given(string key, string value) =>
        _settings.GetSettingNoTracking(key).Returns(new SettingsRow { Type = key, Value = value });

    [Test]
    public async Task WithoutAModeSetting_TheLoopOnlyCollects()
    {
        var options = await _provider.GetAsync();

        options.Mode.ShouldBe(SkillLearningMode.Collect);
    }

    [TestCase("Gate", SkillLearningMode.Gate)]
    [TestCase("gate", SkillLearningMode.Gate)]
    [TestCase(" AutoApply ", SkillLearningMode.AutoApply)]
    [TestCase("COLLECT", SkillLearningMode.Collect)]
    public async Task AKnownModeName_IsReadCaseInsensitively(string value, SkillLearningMode expected)
    {
        Given(SettingsKeys.KLACKSY_LEARNING_MODE, value);

        (await _provider.GetAsync()).Mode.ShouldBe(expected);
    }

    [TestCase("1")]
    [TestCase("2")]
    [TestCase("Apply")]
    [TestCase("")]
    public async Task ANumericOrUnknownMode_FallsBackToCollect(string value)
    {
        Given(SettingsKeys.KLACKSY_LEARNING_MODE, value);

        (await _provider.GetAsync()).Mode.ShouldBe(SkillLearningMode.Collect);
    }

    [Test]
    public async Task WithoutANetGainSetting_TheDefaultApplies()
    {
        (await _provider.GetAsync()).GateMinNetGain.ShouldBe(SkillLearningDefaults.GateMinNetGain);
    }

    [TestCase("0")]
    [TestCase("-2")]
    [TestCase("x")]
    public async Task ANonPositiveNetGain_FallsBackToTheDefault(string value)
    {
        Given(SettingsKeys.KLACKSY_LEARNING_GATE_MIN_NET_GAIN, value);

        (await _provider.GetAsync()).GateMinNetGain.ShouldBe(SkillLearningDefaults.GateMinNetGain);
    }

    [Test]
    public async Task ARaisedNetGain_IsWhatTheGateGets()
    {
        Given(SettingsKeys.KLACKSY_LEARNING_GATE_MIN_NET_GAIN, "3");

        (await _provider.GetAsync()).GateMinNetGain.ShouldBe(3);
    }

    // Neither an explicit override nor a database default model exists - callers must treat this exactly
    // like "no reference run", never guess a hardcoded model name.
    [Test]
    public async Task WithoutASettingOrADefaultModel_ReferenceModelIsNull()
    {
        (await _provider.GetAsync()).ReferenceModel.ShouldBeNull();
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task ABlankReferenceModelSetting_FallsBackToTheDatabasesDefaultModel(string value)
    {
        Given(SettingsKeys.KLACKSY_LEARNING_REFERENCE_MODEL, value);
        _llm.GetDefaultModelAsync().Returns(new LLMModel { ModelId = "deepseek-flash" });

        (await _provider.GetAsync()).ReferenceModel.ShouldBe("deepseek-flash");
    }

    [Test]
    public async Task WithoutASettingButWithADefaultModel_TheDefaultModelsIdIsUsed()
    {
        _llm.GetDefaultModelAsync().Returns(new LLMModel { ModelId = "deepseek-flash" });

        (await _provider.GetAsync()).ReferenceModel.ShouldBe("deepseek-flash");
    }

    // The setting is an override: it wins even when a database default model also exists.
    [Test]
    public async Task AConfiguredReferenceModel_IsTrimmedAndUsedOverTheDefaultModel()
    {
        Given(SettingsKeys.KLACKSY_LEARNING_REFERENCE_MODEL, " deepseek-v4-pro ");
        _llm.GetDefaultModelAsync().Returns(new LLMModel { ModelId = "deepseek-flash" });

        (await _provider.GetAsync()).ReferenceModel.ShouldBe("deepseek-v4-pro");
    }
}
