// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests that the chat write path protects secrets the same way the REST write path does: a settings
/// skill that receives an API key stores it encrypted, leaves ordinary values untouched, and still
/// reports the write as verified even though the stored value no longer equals what was typed.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using SettingsConstants = Klacks.Api.Application.Constants.Settings;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SettingsWriterSkillEncryptionTests
{
    private const string PlainApiKey = "ors-live-key-77c1d4";
    private const string EncryptedPrefix = "ENC:";

    private Dictionary<string, SettingsModel> _store = null!;
    private UpdateOpenRouteSettingsSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        _store = new Dictionary<string, SettingsModel>();

        var repository = Substitute.For<ISettingsRepository>();
        repository.AddSetting(Arg.Do<SettingsModel>(s => _store[s.Type] = s))
            .Returns(ci => ci.Arg<SettingsModel>());
        repository.PutSetting(Arg.Do<SettingsModel>(s => _store[s.Type] = s))
            .Returns(ci => ci.Arg<SettingsModel>());
        repository.GetSetting(Arg.Any<string>())
            .Returns(ci => _store.TryGetValue(ci.Arg<string>(), out var s) ? s : null);

        var encryptionService = new SettingsEncryptionService(
            new StubProtectionProvider(),
            Substitute.For<ILogger<SettingsEncryptionService>>());

        _skill = new UpdateOpenRouteSettingsSkill(
            repository, Substitute.For<IUnitOfWork>(), encryptionService);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin }
    };

    [Test]
    public async Task ApiKeyWrittenViaChat_IsStoredEncrypted()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["apiKey"] = PlainApiKey
        });

        result.Success.ShouldBeTrue(
            "encrypting the value must not break the read-back verification: " + result.Message);

        var stored = _store[SettingsConstants.OPENROUTESERVICE_API_KEY].Value;

        stored.ShouldStartWith(EncryptedPrefix);
        stored.ShouldNotContain(PlainApiKey);
    }

    [Test]
    public async Task OrdinaryValue_IsStoredUnchanged()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["minTravelTimeByCar"] = 15
        });

        result.Success.ShouldBeTrue();
        _store[SettingsConstants.ROUTE_MIN_TRAVEL_TIME_BY_CAR].Value.ShouldBe("15",
            "non-secret settings must stay readable in the database");
    }

    [Test]
    public async Task AlreadyEncryptedValue_IsNotEncryptedTwice()
    {
        await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["apiKey"] = PlainApiKey
        });

        var afterFirstWrite = _store[SettingsConstants.OPENROUTESERVICE_API_KEY].Value;

        await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["apiKey"] = afterFirstWrite
        });

        var afterSecondWrite = _store[SettingsConstants.OPENROUTESERVICE_API_KEY].Value;

        afterSecondWrite.ShouldBe(afterFirstWrite,
            "a value that already carries the ENC: prefix must pass through unchanged");
        afterSecondWrite.Split(EncryptedPrefix, StringSplitOptions.RemoveEmptyEntries).Length.ShouldBe(1,
            "double encryption would make the value unreadable");
    }

    private sealed class StubProtectionProvider : IDataProtectionProvider, IDataProtector
    {
        private const byte Mask = 0x5A;

        public IDataProtector CreateProtector(string purpose) => this;

        public byte[] Protect(byte[] plaintext) => Transform(plaintext);

        public byte[] Unprotect(byte[] protectedData) => Transform(protectedData);

        private static byte[] Transform(byte[] data)
        {
            var result = new byte[data.Length];
            for (var i = 0; i < data.Length; i++)
            {
                result[i] = (byte)(data[i] ^ Mask);
            }

            return result;
        }
    }
}
