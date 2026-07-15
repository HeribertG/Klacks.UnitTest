// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for DefaultShiftMacroResolver: resolves the active macro carrying the preferred standard
/// function for category Shift (StandardAdditive when SURCHARGE_STACKING_MODE is "additive", Standard
/// otherwise, falling back to Standard), or null when none is configured — the resolver never throws.
/// </summary>

using Klacks.Api.Application.Common;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Common;

[TestFixture]
public class DefaultShiftMacroResolverTests
{
    private ISettingsRepository _settingsRepository = null!;
    private DefaultShiftMacroResolver _resolver = null!;

    [SetUp]
    public void Setup()
    {
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _resolver = new DefaultShiftMacroResolver(_settingsRepository);
    }

    [Test]
    public async Task ResolveDefaultMacroIdAsync_WithActiveStandardMacroForShiftCategory_ReturnsItsId()
    {
        var standardMacro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "AllShift",
            Category = MacroCategoryEnum.Shift,
            Type = (int)MacroFunctionEnum.Standard
        };
        var otherMacro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "Vacation",
            Category = MacroCategoryEnum.Vacation,
            Type = (int)MacroFunctionEnum.Standard
        };
        _settingsRepository.GetMacroList().Returns(new List<Macro> { otherMacro, standardMacro });

        var result = await _resolver.ResolveDefaultMacroIdAsync();

        result.ShouldBe(standardMacro.Id);
    }

    [Test]
    public async Task ResolveDefaultMacroIdAsync_WithoutStandardMacroConfigured_ReturnsNull()
    {
        var customMacro = new Macro
        {
            Id = Guid.NewGuid(),
            Name = "AllShift",
            Category = MacroCategoryEnum.Shift,
            Type = (int)MacroFunctionEnum.Custom
        };
        _settingsRepository.GetMacroList().Returns(new List<Macro> { customMacro });

        var result = await _resolver.ResolveDefaultMacroIdAsync();

        result.ShouldBeNull();
    }

    [Test]
    public async Task ResolveDefaultMacroIdAsync_WithNoMacrosAtAll_ReturnsNull()
    {
        _settingsRepository.GetMacroList().Returns(new List<Macro>());

        var result = await _resolver.ResolveDefaultMacroIdAsync();

        result.ShouldBeNull();
    }

    [Test]
    public async Task ResolveDefaultMacroIdAsync_AdditiveStackingConfigured_PrefersStandardAdditiveMacro()
    {
        var standardMacro = BuildShiftMacro("AllShift", MacroFunctionEnum.Standard);
        var additiveMacro = BuildShiftMacro("AllShiftAdditive", MacroFunctionEnum.StandardAdditive);
        _settingsRepository.GetMacroList().Returns(new List<Macro> { standardMacro, additiveMacro });
        SetStackingModeSetting(SurchargeStackingModeValues.Additive);

        var result = await _resolver.ResolveDefaultMacroIdAsync();

        result.ShouldBe(additiveMacro.Id);
    }

    [Test]
    public async Task ResolveDefaultMacroIdAsync_AdditiveStackingConfiguredButNoAdditiveMacro_FallsBackToStandard()
    {
        var standardMacro = BuildShiftMacro("AllShift", MacroFunctionEnum.Standard);
        _settingsRepository.GetMacroList().Returns(new List<Macro> { standardMacro });
        SetStackingModeSetting(SurchargeStackingModeValues.Additive);

        var result = await _resolver.ResolveDefaultMacroIdAsync();

        result.ShouldBe(standardMacro.Id);
    }

    [Test]
    public async Task ResolveDefaultMacroIdAsync_HighestWinsStackingConfigured_ResolvesStandardMacro()
    {
        var standardMacro = BuildShiftMacro("AllShift", MacroFunctionEnum.Standard);
        var additiveMacro = BuildShiftMacro("AllShiftAdditive", MacroFunctionEnum.StandardAdditive);
        _settingsRepository.GetMacroList().Returns(new List<Macro> { standardMacro, additiveMacro });
        SetStackingModeSetting(SurchargeStackingModeValues.HighestWins);

        var result = await _resolver.ResolveDefaultMacroIdAsync();

        result.ShouldBe(standardMacro.Id);
    }

    private void SetStackingModeSetting(string value)
    {
        _settingsRepository.GetSettingNoTracking(SettingKeys.SurchargeStackingMode)
            .Returns(new Klacks.Api.Domain.Models.Settings.Settings
            {
                Type = SettingKeys.SurchargeStackingMode,
                Value = value,
            });
    }

    private static Macro BuildShiftMacro(string name, MacroFunctionEnum function) => new()
    {
        Id = Guid.NewGuid(),
        Name = name,
        Category = MacroCategoryEnum.Shift,
        Type = (int)function,
    };
}
