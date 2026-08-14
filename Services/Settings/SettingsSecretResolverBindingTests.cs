// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SettingsSecretResolver.ResolveBoundAsync: a stored secret is only released when the
/// request still points at the stored server and user. Without that binding a caller could send a
/// foreign host plus the masked placeholder and have the server mail the stored password to it.
/// </summary>

using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Services.Settings;
using Shouldly;

namespace Klacks.UnitTest.Services.Settings;

[TestFixture]
public class SettingsSecretResolverBindingTests
{
    private const string HostSettingType = "incomingserver";
    private const string UserSettingType = "incomingserverUsername";
    private const string PasswordSettingType = "incomingserverPassword";

    private const string StoredHost = "mail.firma.ch";
    private const string StoredUser = "info@firma.ch";
    private const string StoredPasswordCipher = "ENC:cipher";
    private const string StoredPasswordPlain = "the-company-mailbox-password";
    private const string AttackerHost = "collector.attacker.example";

    private ISettingsReader _settingsReader = null!;
    private ISettingsEncryptionService _encryptionService = null!;
    private SettingsSecretResolver _resolver = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsReader = Substitute.For<ISettingsReader>();
        _encryptionService = Substitute.For<ISettingsEncryptionService>();
        _encryptionService.Decrypt(StoredPasswordCipher).Returns(StoredPasswordPlain);

        _resolver = new SettingsSecretResolver(_settingsReader, _encryptionService);
    }

    [Test]
    public async Task ResolveBoundAsync_WhenNothingIsStored_ShouldReturnEmpty()
    {
        ArrangeSetting(HostSettingType, null);
        ArrangeSetting(UserSettingType, null);
        ArrangeSetting(PasswordSettingType, null);

        var result = await ResolveAsync(StoredHost, StoredUser, SettingsMasking.MaskedValue);

        result.ShouldBeEmpty("a fresh install has no stored host, so nothing may match");
    }

    [Test]
    public async Task ResolveBoundAsync_WhenHostAndUserMatch_ShouldReleaseTheStoredSecret()
    {
        ArrangeStoredConfiguration();

        var result = await ResolveAsync(StoredHost, StoredUser, SettingsMasking.MaskedValue);

        result.ShouldBe(StoredPasswordPlain);
    }

    [Test]
    public async Task ResolveBoundAsync_WithAForeignHost_ShouldNotReleaseTheStoredSecret()
    {
        ArrangeStoredConfiguration();

        var result = await ResolveAsync(AttackerHost, StoredUser, SettingsMasking.MaskedValue);

        result.ShouldBeEmpty("this is the credential exfiltration path and must stay closed");
        result.ShouldNotBe(StoredPasswordPlain);
    }

    [Test]
    public async Task ResolveBoundAsync_WithAForeignUser_ShouldNotReleaseTheStoredSecret()
    {
        ArrangeStoredConfiguration();

        var result = await ResolveAsync(StoredHost, "someone.else@firma.ch", SettingsMasking.MaskedValue);

        result.ShouldBeEmpty();
    }

    [TestCase("MAIL.FIRMA.CH")]
    [TestCase("mail.firma.ch.")]
    [TestCase("  mail.firma.ch  ")]
    public async Task ResolveBoundAsync_WithAnEquivalentHostSpelling_ShouldStillReleaseTheStoredSecret(string host)
    {
        ArrangeStoredConfiguration();

        var result = await ResolveAsync(host, StoredUser, SettingsMasking.MaskedValue);

        result.ShouldBe(StoredPasswordPlain, "case, trailing dot and padding must not break a legitimate test");
    }

    [Test]
    public async Task ResolveBoundAsync_WhenTheClientSendsItsOwnPassword_ShouldPassItThroughForAnyHost()
    {
        ArrangeStoredConfiguration();
        const string ownPassword = "a-password-the-caller-typed";

        var result = await ResolveAsync(AttackerHost, StoredUser, ownPassword);

        result.ShouldBe(ownPassword, "testing a foreign server with your own credentials stays allowed");
    }

    [Test]
    public async Task ResolveAsync_WithoutBinding_ShouldKeepTheExistingBehaviour()
    {
        ArrangeStoredConfiguration();

        var result = await _resolver.ResolveAsync(PasswordSettingType, SettingsMasking.MaskedValue);

        result.ShouldBe(StoredPasswordPlain);
    }

    private Task<string> ResolveAsync(string host, string user, string password) =>
        _resolver.ResolveBoundAsync(
            PasswordSettingType,
            password,
            new SecretBinding(HostSettingType, host),
            new SecretBinding(UserSettingType, user));

    private void ArrangeStoredConfiguration()
    {
        ArrangeSetting(HostSettingType, StoredHost);
        ArrangeSetting(UserSettingType, StoredUser);
        ArrangeSetting(PasswordSettingType, StoredPasswordCipher);
    }

    private void ArrangeSetting(string type, string? value)
    {
        _settingsReader.GetSetting(type).Returns(value == null
            ? null
            : new Klacks.Api.Domain.Models.Settings.Settings { Type = type, Value = value });
    }
}
