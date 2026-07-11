// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for PayrollExportConfigRepository against an in-memory EF Core database, verifying
/// that an existing config row wins over the DEFAULT_PAYROLL_TARGET_SYSTEM setting and that the
/// generated default falls back to DATEV when the setting is missing, blank or unknown.
/// </summary>
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Exports;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Repositories.Exports;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;
using Shouldly;
using SettingsEntity = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Infrastructure.Repositories.Exports;

[TestFixture]
public class PayrollExportConfigRepositoryTests
{
    private DataBaseContext _context = null!;
    private ISettingsReader _settingsReader = null!;
    private PayrollExportConfigRepository _repository = null!;

    [SetUp]
    public void Setup()
    {
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options;
        _context = new DataBaseContext(options, Substitute.For<IHttpContextAccessor>());
        _settingsReader = Substitute.For<ISettingsReader>();

        var meritPalkFormatter = Substitute.For<IPayrollExportFormatter>();
        meritPalkFormatter.FormatKey.Returns(PayrollExportConstants.FormatKeyMeritPalkEe);
        var datevFormatter = Substitute.For<IPayrollExportFormatter>();
        datevFormatter.FormatKey.Returns(PayrollExportConstants.FormatKeyDatevLug);

        _repository = new PayrollExportConfigRepository(
            _context,
            _settingsReader,
            new[] { datevFormatter, meritPalkFormatter },
            Substitute.For<ILogger<PayrollExportConfigRepository>>());
    }

    [TearDown]
    public void TearDown()
    {
        _context.Dispose();
    }

    [Test]
    public async Task GetByGroupAsync_ConfigRowExists_IgnoresDefaultTargetSystemSetting()
    {
        var groupId = Guid.NewGuid();
        _context.PayrollExportGroupConfig.Add(new PayrollExportGroupConfig
        {
            Id = Guid.NewGuid(),
            GroupId = groupId,
            TargetSystem = PayrollExportConstants.FormatKeyPaxmlSe,
        });
        await _context.SaveChangesAsync();
        StubSettingValue(PayrollExportConstants.FormatKeyMeritPalkEe);

        var result = await _repository.GetByGroupAsync(groupId);

        result.TargetSystem.ShouldBe(PayrollExportConstants.FormatKeyPaxmlSe);
        await _settingsReader.DidNotReceive().GetSetting(Arg.Any<string>());
    }

    [Test]
    public async Task GetByGroupAsync_NoRowAndSettingSet_UsesSettingAsTargetSystem()
    {
        StubSettingValue(PayrollExportConstants.FormatKeyMeritPalkEe);

        var result = await _repository.GetByGroupAsync(Guid.NewGuid());

        result.TargetSystem.ShouldBe(PayrollExportConstants.FormatKeyMeritPalkEe);
        result.Delimiter.ShouldBe(PayrollExportConstants.DefaultDelimiter);
        result.Encoding.ShouldBe(PayrollExportConstants.DefaultEncoding);
    }

    [Test]
    public async Task GetByGroupAsync_NoRowAndNoSetting_FallsBackToDatev()
    {
        _settingsReader.GetSetting(SettingKeys.DefaultPayrollTargetSystem)
            .Returns(Task.FromResult<SettingsEntity?>(null));

        var result = await _repository.GetByGroupAsync(Guid.NewGuid());

        result.TargetSystem.ShouldBe(PayrollExportConstants.FormatKeyDatevLug);
    }

    [Test]
    public async Task GetByGroupAsync_NoRowAndBlankSetting_FallsBackToDatev()
    {
        StubSettingValue("   ");

        var result = await _repository.GetByGroupAsync(Guid.NewGuid());

        result.TargetSystem.ShouldBe(PayrollExportConstants.FormatKeyDatevLug);
    }

    [Test]
    public async Task GetByGroupAsync_NoRowAndUnknownSettingValue_FallsBackToDatev()
    {
        StubSettingValue("not-a-known-format");

        var result = await _repository.GetByGroupAsync(Guid.NewGuid());

        result.TargetSystem.ShouldBe(PayrollExportConstants.FormatKeyDatevLug);
    }

    [Test]
    public async Task GetByGroupAsync_NoRowAndSettingWithDifferentCasing_UsesCanonicalFormatKey()
    {
        StubSettingValue(PayrollExportConstants.FormatKeyMeritPalkEe.ToUpperInvariant());

        var result = await _repository.GetByGroupAsync(Guid.NewGuid());

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
