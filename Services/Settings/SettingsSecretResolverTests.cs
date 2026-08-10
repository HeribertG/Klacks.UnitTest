// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Services.Settings;
using ApiSettings = Klacks.Api.Application.Constants.Settings;
using SettingRow = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Services.Settings;

[TestFixture]
public class SettingsSecretResolverTests
{
    private const string StoredCipher = "ENC:cipher";
    private const string StoredPlain = "DNQK3BPDHELWC5C5YBQA";

    private ISettingsReader _settingsReader = null!;
    private ISettingsEncryptionService _encryptionService = null!;
    private SettingsSecretResolver _sut = null!;

    [SetUp]
    public void Setup()
    {
        _settingsReader = Substitute.For<ISettingsReader>();
        _encryptionService = Substitute.For<ISettingsEncryptionService>();
        _sut = new SettingsSecretResolver(_settingsReader, _encryptionService);

        _settingsReader.GetSetting(ApiSettings.APP_OUTGOING_SERVER_PASSWORD)
            .Returns(new SettingRow
            {
                Type = ApiSettings.APP_OUTGOING_SERVER_PASSWORD,
                Value = StoredCipher
            });
        _encryptionService.Decrypt(StoredCipher).Returns(StoredPlain);
    }

    [Test]
    public async Task MaskedPlaceholder_FallsBackToTheStoredSecret()
    {
        var secret = await _sut.ResolveAsync(
            ApiSettings.APP_OUTGOING_SERVER_PASSWORD,
            SettingsMasking.MaskedValue);

        Assert.That(secret, Is.EqualTo(StoredPlain));
    }

    [Test]
    public async Task EmptyValue_FallsBackToTheStoredSecret()
    {
        var secret = await _sut.ResolveAsync(ApiSettings.APP_OUTGOING_SERVER_PASSWORD, string.Empty);

        Assert.That(secret, Is.EqualTo(StoredPlain));
    }

    [Test]
    public async Task ValueTypedByTheUser_IsUsedUnchanged()
    {
        var secret = await _sut.ResolveAsync(ApiSettings.APP_OUTGOING_SERVER_PASSWORD, "typed-by-user");

        Assert.That(secret, Is.EqualTo("typed-by-user"));
        await _settingsReader.DidNotReceive().GetSetting(Arg.Any<string>());
    }

    [Test]
    public async Task MissingSetting_ReturnsEmptyInsteadOfThePlaceholder()
    {
        _settingsReader.GetSetting(ApiSettings.APP_INCOMING_SERVER_PASSWORD)
            .Returns((SettingRow?)null);

        var secret = await _sut.ResolveAsync(
            ApiSettings.APP_INCOMING_SERVER_PASSWORD,
            SettingsMasking.MaskedValue);

        Assert.That(secret, Is.EqualTo(string.Empty));
    }
}
