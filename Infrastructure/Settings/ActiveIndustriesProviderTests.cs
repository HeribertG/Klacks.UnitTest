// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Constants;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Settings;

[TestFixture]
public class ActiveIndustriesProviderTests
{
    private ISettingsReader _settingsReader = null!;
    private ActiveIndustriesProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsReader = Substitute.For<ISettingsReader>();
        _provider = new ActiveIndustriesProvider(_settingsReader);
    }

    [Test]
    public async Task GetActiveIndustrySlugsAsync_SettingMissing_ReturnsNull()
    {
        _settingsReader.GetSetting(SettingKeys.ActiveIndustries).Returns((SettingsModel?)null);

        var result = await _provider.GetActiveIndustrySlugsAsync();

        result.ShouldBeNull();
    }

    [TestCase("")]
    [TestCase("   ")]
    public async Task GetActiveIndustrySlugsAsync_SettingBlank_ReturnsNull(string value)
    {
        _settingsReader.GetSetting(SettingKeys.ActiveIndustries)
            .Returns(new SettingsModel { Id = Guid.NewGuid(), Type = SettingKeys.ActiveIndustries, Value = value });

        var result = await _provider.GetActiveIndustrySlugsAsync();

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetActiveIndustrySlugsAsync_SingleSlug_ReturnsIt()
    {
        _settingsReader.GetSetting(SettingKeys.ActiveIndustries)
            .Returns(new SettingsModel { Id = Guid.NewGuid(), Type = SettingKeys.ActiveIndustries, Value = "healthcare" });

        var result = await _provider.GetActiveIndustrySlugsAsync();

        result.ShouldNotBeNull();
        result.ShouldBe(new[] { "healthcare" });
    }

    [Test]
    public async Task GetActiveIndustrySlugsAsync_MixedCaseWithWhitespaceAndDuplicates_NormalizesToLowercaseDistinct()
    {
        _settingsReader.GetSetting(SettingKeys.ActiveIndustries)
            .Returns(new SettingsModel
            {
                Id = Guid.NewGuid(),
                Type = SettingKeys.ActiveIndustries,
                Value = " Healthcare , SECURITY ,, security ",
            });

        var result = await _provider.GetActiveIndustrySlugsAsync();

        result.ShouldNotBeNull();
        result.ShouldBe(new[] { "healthcare", "security" });
    }

    [Test]
    public async Task GetActiveIndustrySlugsAsync_OnlySeparators_ReturnsNull()
    {
        _settingsReader.GetSetting(SettingKeys.ActiveIndustries)
            .Returns(new SettingsModel { Id = Guid.NewGuid(), Type = SettingKeys.ActiveIndustries, Value = ", ," });

        var result = await _provider.GetActiveIndustrySlugsAsync();

        result.ShouldBeNull();
    }
}
