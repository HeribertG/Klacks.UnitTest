// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for PayrollExportConfigRepository, verifying that the installation-wide default comes from the
/// DEFAULT_PAYROLL_TARGET_SYSTEM setting and falls back to DATEV when the setting is missing, blank or unknown.
/// </summary>
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Exports;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Infrastructure.Repositories.Exports;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Repositories.Exports;

[TestFixture]
public class PayrollExportConfigRepositoryTests
{
    private ISettingsReader _settingsReader = null!;
    private PayrollExportConfigRepository _repository = null!;

    [SetUp]
    public void Setup()
    {
        _settingsReader = Substitute.For<ISettingsReader>();

        var meritPalkFormatter = Substitute.For<IPayrollExportFormatter>();
        meritPalkFormatter.FormatKey.Returns(PayrollExportConstants.FormatKeyMeritPalkEe);
        var datevFormatter = Substitute.For<IPayrollExportFormatter>();
        datevFormatter.FormatKey.Returns(PayrollExportConstants.FormatKeyDatevLug);

        _repository = new PayrollExportConfigRepository(
            _settingsReader,
            new[] { datevFormatter, meritPalkFormatter },
            Substitute.For<ILogger<PayrollExportConfigRepository>>());
    }

    [Test]
    public async Task GetAsync_SettingSet_UsesSettingAsTargetSystem()
    {
        StubSettingValue(PayrollExportConstants.FormatKeyMeritPalkEe);

        var result = await _repository.GetAsync();

        result.TargetSystem.ShouldBe(PayrollExportConstants.FormatKeyMeritPalkEe);
        result.Delimiter.ShouldBe(PayrollExportConstants.DefaultDelimiter);
        result.Encoding.ShouldBe(PayrollExportConstants.DefaultEncoding);
    }

    [Test]
    public async Task GetAsync_NoSetting_FallsBackToDatev()
    {
        _settingsReader.GetSetting(SettingKeys.DefaultPayrollTargetSystem)
            .Returns(Task.FromResult<SettingsEntity?>(null));

        var result = await _repository.GetAsync();

        result.TargetSystem.ShouldBe(PayrollExportConstants.FormatKeyDatevLug);
    }

    [Test]
    public async Task GetAsync_BlankSetting_FallsBackToDatev()
    {
        StubSettingValue("   ");

        var result = await _repository.GetAsync();

        result.TargetSystem.ShouldBe(PayrollExportConstants.FormatKeyDatevLug);
    }

    [Test]
    public async Task GetAsync_UnknownSettingValue_FallsBackToDatev()
    {
        StubSettingValue("not-a-known-format");

        var result = await _repository.GetAsync();

        result.TargetSystem.ShouldBe(PayrollExportConstants.FormatKeyDatevLug);
    }

    [Test]
    public async Task GetAsync_SettingWithDifferentCasing_UsesCanonicalFormatKey()
    {
        StubSettingValue(PayrollExportConstants.FormatKeyMeritPalkEe.ToUpperInvariant());

        var result = await _repository.GetAsync();

        result.TargetSystem.ShouldBe(PayrollExportConstants.FormatKeyMeritPalkEe);
    }

    private void StubSettingValue(string value)
    {
        _settingsReader.GetSetting(SettingKeys.DefaultPayrollTargetSystem)
            .Returns(Task.FromResult<SettingsEntity?>(new SettingsEntity
            {
                Id = Guid.NewGuid(),
                Type = SettingKeys.DefaultPayrollTargetSystem,
                Value = value,
            }));
    }
}
