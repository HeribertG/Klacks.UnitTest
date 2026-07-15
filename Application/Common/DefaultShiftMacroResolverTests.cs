// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for DefaultShiftMacroResolver: resolves the active macro flagged as the Standard
/// function for category Shift, or null when none is configured — the resolver never throws or
/// invents a fallback.
/// </summary>

using Klacks.Api.Application.Common;

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
}
