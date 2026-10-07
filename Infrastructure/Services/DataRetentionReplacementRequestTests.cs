// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the replacement request retention step of DataRetentionBackgroundService: rows reported longer ago
/// than REPLACEMENT_REQUEST_RETENTION_DAYS (default 730) are physically deleted with the cutoff computed from the
/// injected clock, and a failing step never stops the physical purge that follows.
/// </summary>

using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using SettingKeys = Klacks.Api.Application.Constants.Settings;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Services;

[TestFixture]
public class DataRetentionReplacementRequestTests
{
    private static readonly DateTime Now = new(2026, 10, 7, 3, 0, 0, DateTimeKind.Utc);

    private IReplacementRequestRepository _repository = null!;
    private ISettingsReader _settingsReader = null!;
    private ServiceProvider _services = null!;
    private DataRetentionBackgroundService _service = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IReplacementRequestRepository>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _settingsReader.GetSetting(Arg.Any<string>()).Returns((SettingsEntity?)null);

        _services = new ServiceCollection()
            .AddSingleton<TimeProvider>(new SettableTimeProvider(Now))
            .AddSingleton(_repository)
            .AddSingleton(_settingsReader)
            .BuildServiceProvider();
        _service = new DataRetentionBackgroundService(
            Substitute.For<IServiceScopeFactory>(), Substitute.For<ILogger<DataRetentionBackgroundService>>());
    }

    [TearDown]
    public void TearDown()
    {
        _service.Dispose();
        _services.Dispose();
    }

    [Test]
    public async Task DefaultRetention_DeletesRowsReportedMoreThan730DaysAgo()
    {
        await _service.DeleteExpiredReplacementRequestsAsync(_services, CancellationToken.None);

        await _repository.Received(1).DeleteReportedBeforeAsync(Now.AddDays(-730), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ConfiguredRetention_IsUsedForTheCutoff()
    {
        _settingsReader.GetSetting(SettingKeys.REPLACEMENT_REQUEST_RETENTION_DAYS)
            .Returns(new SettingsEntity { Type = SettingKeys.REPLACEMENT_REQUEST_RETENTION_DAYS, Value = "100" });

        await _service.DeleteExpiredReplacementRequestsAsync(_services, CancellationToken.None);

        await _repository.Received(1).DeleteReportedBeforeAsync(Now.AddDays(-100), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FailingStep_IsLoggedAndSwallowed()
    {
        _repository.DeleteReportedBeforeAsync(Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw new InvalidOperationException("db down"));

        await Should.NotThrowAsync(() => _service.DeleteExpiredReplacementRequestsAsync(_services, CancellationToken.None));
    }
}
