// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Architecture guard pinning that the proactive kill switch stays an UNENCRYPTED settings row.
/// The proactive plan requires Klacksy's whole memory - ledger, governance, kill switch - to survive a
/// database dump transfer onto another machine, and DataProtection keys do not travel with a dump: an
/// encrypted value would come back as unreadable ciphertext, and the resolver would then fall back to
/// "kill switch off" on a system whose administrator had switched it on. Adding the key to
/// SettingsEncryptionService.SensitiveSettingTypes is the silent way to break that, which is why it is
/// asserted here rather than left to review.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Settings;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ProactiveGovernanceKillSwitchGuardTests
{
    private SettingsEncryptionService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        var provider = Substitute.For<IDataProtectionProvider>();
        provider.CreateProtector(Arg.Any<string>()).Returns(Substitute.For<IDataProtector>());
        _sut = new SettingsEncryptionService(
            provider, Substitute.For<ILogger<SettingsEncryptionService>>());
    }

    [Test]
    public void KillSwitchSetting_IsNotEncrypted()
    {
        // Act
        var isSensitive = _sut.IsSensitiveSettingType(SettingKeys.KlacksyProactiveKillSwitch);

        // Assert
        isSensitive.ShouldBeFalse(
            $"{SettingKeys.KlacksyProactiveKillSwitch} must stay readable after a plain database dump " +
            "transfer; DataProtection keys do not travel with the dump.");
    }

    [Test]
    public void KillSwitchSetting_PassesThroughStorageUnchanged()
    {
        // Act
        var stored = _sut.ProcessForStorage(SettingKeys.KlacksyProactiveKillSwitch, "true");

        // Assert
        stored.ShouldBe("true");
    }
}
