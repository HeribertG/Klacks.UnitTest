// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests that the settings list distinguishes "a secret is stored" from "the stored secret can no
/// longer be read". After a key ring change the ciphertext is still in the database, and showing the
/// usual *** placeholder would tell the operator everything is fine while mail delivery is dead.
/// An empty field is what makes the re-entry obvious without reading container logs.
/// </summary>

using Klacks.Api.Application.Handlers.Settings.Setting;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Settings.Settings;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Settings;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Klacks.UnitTest.Services.Settings;

[TestFixture]
public class SettingsListMaskingTests
{
    private const string SecretType = "outgoingserverPassword";
    private const string StoredCipher = "ENC:cipher-from-a-previous-key-ring";

    private ISettingsRepository _repository = null!;
    private ISettingsEncryptionService _encryptionService = null!;
    private ListQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ISettingsRepository>();
        _encryptionService = Substitute.For<ISettingsEncryptionService>();
        _encryptionService.IsServerOnlySettingType(SecretType).Returns(true);
        _encryptionService.IsSensitiveSettingType(SecretType).Returns(true);

        _repository.GetSettingsList().Returns(new List<Klacks.Api.Domain.Models.Settings.Settings>
        {
            new() { Type = SecretType, Value = StoredCipher },
        });

        _handler = new ListQueryHandler(_repository, _encryptionService, Substitute.For<ILogger<ListQueryHandler>>());
    }

    [Test]
    public async Task Handle_WhenTheSecretIsReadable_ShouldReturnTheMask()
    {
        _encryptionService.ProcessForReading(SecretType, StoredCipher).Returns("the-real-password");

        var result = await _handler.Handle(new ListQuery(), CancellationToken.None);

        result.Single().Value.ShouldBe(SettingsMasking.MaskedValue,
            "a readable secret stays hidden behind the placeholder");
    }

    [Test]
    public async Task Handle_WhenTheSecretCanNoLongerBeDecrypted_ShouldReturnEmptyNotTheMask()
    {
        // Decrypt returns string.Empty when the key that encrypted the value is gone.
        _encryptionService.ProcessForReading(SecretType, StoredCipher).Returns(string.Empty);

        var result = await _handler.Handle(new ListQuery(), CancellationToken.None);

        var value = result.Single().Value;

        value.ShouldBeEmpty(
            "an unreadable secret must look unset, so the operator retypes it");
        value.ShouldNotBe(SettingsMasking.MaskedValue,
            "showing *** here would claim the mail password is fine while delivery is dead");
    }
}
