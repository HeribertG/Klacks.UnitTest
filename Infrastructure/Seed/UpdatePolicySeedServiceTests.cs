// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the startup seed of the initial auto-update policy: nothing is written without installer configuration
/// (vendor production, development, tests), configured values are seeded only where no setting exists, a saved
/// admin choice is never overwritten, invalid values are skipped, and a concurrent seed by a second instance is
/// tolerated.
/// </summary>

using Klacks.Api.Application.Configuration;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Infrastructure.Persistence.Seed;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using SettingsConstants = Klacks.Api.Application.Constants.Settings;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Seed;

[TestFixture]
public class UpdatePolicySeedServiceTests
{
    private ISettingsRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ISettingsRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        GivenExistingSettings(new Dictionary<string, string>());
    }

    [Test]
    public async Task SeedAsync_WithoutInstallerConfiguration_DoesNotTouchTheDatabase()
    {
        var seeded = await CreateService(new UpdateInitialPolicyOptions()).SeedAsync();

        seeded.ShouldBe(0);
        await _repository.DidNotReceiveWithAnyArgs().GetSettingsByTypesAsync(default!, default);
        await _repository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task SeedAsync_OnAFreshDatabase_SeedsAutoUpdateOnAndNotifyOnlyOff()
    {
        var seeded = await CreateService(CustomerDefaults()).SeedAsync();

        seeded.ShouldBe(2);
        await _repository.Received(1).AddSetting(Arg.Is<SettingsModel>(s =>
            s.Type == SettingsConstants.UPDATE_AUTO_ENABLED && s.Value == bool.TrueString && s.Id != Guid.Empty));
        await _repository.Received(1).AddSetting(Arg.Is<SettingsModel>(s =>
            s.Type == SettingsConstants.UPDATE_NOTIFY_ONLY && s.Value == bool.FalseString));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task SeedAsync_WhenTheAdminAlreadySavedASetting_NeverOverwritesIt()
    {
        GivenExistingSettings(new Dictionary<string, string>
        {
            [SettingsConstants.UPDATE_AUTO_ENABLED] = bool.FalseString,
        });

        var seeded = await CreateService(CustomerDefaults()).SeedAsync();

        seeded.ShouldBe(1);
        await _repository.DidNotReceive().AddSetting(Arg.Is<SettingsModel>(s => s.Type == SettingsConstants.UPDATE_AUTO_ENABLED));
        await _repository.DidNotReceiveWithAnyArgs().PutSetting(default!);
        await _repository.DidNotReceiveWithAnyArgs().UpsertSettingAsync(default!, default!);
        await _repository.Received(1).AddSetting(Arg.Is<SettingsModel>(s => s.Type == SettingsConstants.UPDATE_NOTIFY_ONLY));
    }

    [Test]
    public async Task SeedAsync_WhenEverySettingExists_WritesNothing()
    {
        GivenExistingSettings(new Dictionary<string, string>
        {
            [SettingsConstants.UPDATE_AUTO_ENABLED] = bool.FalseString,
            [SettingsConstants.UPDATE_NOTIFY_ONLY] = bool.TrueString,
        });

        var seeded = await CreateService(CustomerDefaults()).SeedAsync();

        seeded.ShouldBe(0);
        await _repository.DidNotReceiveWithAnyArgs().AddSetting(default!);
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task SeedAsync_WithOperatorChoiceFalse_SeedsAnExplicitFalse()
    {
        var options = new UpdateInitialPolicyOptions { InitialAutoEnabled = "false" };

        var seeded = await CreateService(options).SeedAsync();

        seeded.ShouldBe(1);
        await _repository.Received(1).AddSetting(Arg.Is<SettingsModel>(s =>
            s.Type == SettingsConstants.UPDATE_AUTO_ENABLED && s.Value == bool.FalseString));
    }

    [TestCase("yes")]
    [TestCase("1")]
    [TestCase("   ")]
    public async Task SeedAsync_WithAnInvalidOrBlankValue_SkipsThatSetting(string configured)
    {
        var options = new UpdateInitialPolicyOptions { InitialAutoEnabled = configured };

        var seeded = await CreateService(options).SeedAsync();

        seeded.ShouldBe(0);
        await _repository.DidNotReceiveWithAnyArgs().AddSetting(default!);
    }

    [Test]
    public async Task SeedAsync_AcceptsCaseAndWhitespaceVariants()
    {
        var options = new UpdateInitialPolicyOptions { InitialAutoEnabled = " TRUE " };

        await CreateService(options).SeedAsync();

        await _repository.Received(1).AddSetting(Arg.Is<SettingsModel>(s =>
            s.Type == SettingsConstants.UPDATE_AUTO_ENABLED && s.Value == bool.TrueString));
    }

    [Test]
    public async Task SeedAsync_WhenAnotherInstanceSeededConcurrently_SwallowsTheDuplicate()
    {
        _unitOfWork.CompleteAsync().Returns(Task.FromException(
            new DatabaseUpdateException("duplicate key", isDuplicate: true)));

        var seeded = await CreateService(CustomerDefaults()).SeedAsync();

        seeded.ShouldBe(0);
    }

    [Test]
    public async Task SeedAsync_OnAnyOtherWriteFailure_Rethrows()
    {
        _unitOfWork.CompleteAsync().Returns(Task.FromException(new DatabaseUpdateException("connection lost")));

        await Should.ThrowAsync<DatabaseUpdateException>(() => CreateService(CustomerDefaults()).SeedAsync());
    }

    private static UpdateInitialPolicyOptions CustomerDefaults() =>
        new() { InitialAutoEnabled = "true", InitialNotifyOnly = "false" };

    private void GivenExistingSettings(IReadOnlyDictionary<string, string> existing)
    {
        _repository.GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(existing);
    }

    private UpdatePolicySeedService CreateService(UpdateInitialPolicyOptions options) =>
        new(Options.Create(options), _repository, _unitOfWork, NullLogger<UpdatePolicySeedService>.Instance);
}
