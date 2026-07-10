// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ExportFormatPolicy: the combined order+payroll catalog, fixed-format handling,
/// the "no setting yet" default-enabled fallback and the enabled/disabled gate.
/// </summary>
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Exports;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Services.Exports;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Exports;

[TestFixture]
public class ExportFormatPolicyTests
{
    private IExportFormatter _orderFormatter = null!;
    private IPayrollExportFormatter _payrollFormatter = null!;
    private ISettingsReader _settingsReader = null!;
    private ExportFormatPolicy _policy = null!;

    [SetUp]
    public void Setup()
    {
        _orderFormatter = Substitute.For<IExportFormatter>();
        _orderFormatter.FormatKey.Returns(ExportConstants.FormatBmd);

        _payrollFormatter = Substitute.For<IPayrollExportFormatter>();
        _payrollFormatter.FormatKey.Returns(PayrollExportConstants.FormatKeyPaxmlSe);

        _settingsReader = Substitute.For<ISettingsReader>();

        _policy = new ExportFormatPolicy([_orderFormatter], [_payrollFormatter], _settingsReader);
    }

    private void ReturnSetting(string? value) =>
        _settingsReader.GetSetting(SettingKeys.EnabledExportFormats)
            .Returns(value == null ? null : new Klacks.Api.Domain.Models.Settings.Settings { Type = SettingKeys.EnabledExportFormats, Value = value });

    [Test]
    public async Task GetCatalogAsync_IncludesBothOrderAndPayrollFormatKeys()
    {
        ReturnSetting(null);

        var catalog = await _policy.GetCatalogAsync(CancellationToken.None);

        catalog.Select(r => r.Key).ShouldContain(ExportConstants.FormatBmd);
        catalog.Select(r => r.Key).ShouldContain(PayrollExportConstants.FormatKeyPaxmlSe);
    }

    [Test]
    public async Task GetCatalogAsync_TagsEachResourceWithItsFamily()
    {
        ReturnSetting(null);

        var catalog = await _policy.GetCatalogAsync(CancellationToken.None);

        catalog.Single(r => r.Key == ExportConstants.FormatBmd).Family.ShouldBe(ExportConstants.FormatFamilyOrder);
        catalog.Single(r => r.Key == PayrollExportConstants.FormatKeyPaxmlSe).Family.ShouldBe(ExportConstants.FormatFamilyPayroll);
    }

    [Test]
    public async Task GetCatalogAsync_GroupsDatevOrderAndPayrollUnderTheSameBrand()
    {
        var datevOrder = Substitute.For<IExportFormatter>();
        datevOrder.FormatKey.Returns(ExportConstants.FormatDatev);
        var datevPayroll = Substitute.For<IPayrollExportFormatter>();
        datevPayroll.FormatKey.Returns(PayrollExportConstants.FormatKeyDatevLug);
        var policy = new ExportFormatPolicy([datevOrder], [datevPayroll], _settingsReader);
        ReturnSetting(null);

        var catalog = await policy.GetCatalogAsync(CancellationToken.None);

        catalog.Single(r => r.Key == ExportConstants.FormatDatev).Brand.ShouldBe(ExportConstants.BrandDatev);
        catalog.Single(r => r.Key == PayrollExportConstants.FormatKeyDatevLug).Brand.ShouldBe(ExportConstants.BrandDatev);
    }

    [Test]
    public async Task GetCatalogAsync_UsesFormatKeyAsBrand_ForSingleVariantFormats()
    {
        ReturnSetting(null);

        var catalog = await _policy.GetCatalogAsync(CancellationToken.None);

        catalog.Single(r => r.Key == ExportConstants.FormatBmd).Brand.ShouldBe(ExportConstants.FormatBmd);
    }

    [Test]
    public async Task GetCatalogAsync_MarksCsvJsonXmlAsFixedAndEnabled()
    {
        var csvFormatter = Substitute.For<IExportFormatter>();
        csvFormatter.FormatKey.Returns(ExportConstants.FormatCsv);
        var policy = new ExportFormatPolicy([csvFormatter, _orderFormatter], [_payrollFormatter], _settingsReader);
        ReturnSetting("");

        var catalog = await policy.GetCatalogAsync(CancellationToken.None);
        var csv = catalog.Single(r => r.Key == ExportConstants.FormatCsv);

        csv.Fixed.ShouldBeTrue();
        csv.Enabled.ShouldBeTrue();
    }

    [Test]
    public async Task GetCatalogAsync_WhenNoSettingExistsYet_TreatsEveryOptionalFormatAsEnabled()
    {
        ReturnSetting(null);

        var catalog = await _policy.GetCatalogAsync(CancellationToken.None);

        catalog.Single(r => r.Key == ExportConstants.FormatBmd).Enabled.ShouldBeTrue();
        catalog.Single(r => r.Key == PayrollExportConstants.FormatKeyPaxmlSe).Enabled.ShouldBeTrue();
    }

    [Test]
    public async Task GetCatalogAsync_WhenSettingNarrowsSelection_DisablesUnlistedOptionalFormats()
    {
        ReturnSetting(ExportConstants.FormatBmd);

        var catalog = await _policy.GetCatalogAsync(CancellationToken.None);

        catalog.Single(r => r.Key == ExportConstants.FormatBmd).Enabled.ShouldBeTrue();
        catalog.Single(r => r.Key == PayrollExportConstants.FormatKeyPaxmlSe).Enabled.ShouldBeFalse();
    }

    [Test]
    public async Task IsEnabledAsync_ReturnsTrue_ForPayrollFormatInEnabledSetting()
    {
        ReturnSetting(PayrollExportConstants.FormatKeyPaxmlSe);

        var enabled = await _policy.IsEnabledAsync(PayrollExportConstants.FormatKeyPaxmlSe, CancellationToken.None);

        enabled.ShouldBeTrue();
    }

    [Test]
    public async Task IsEnabledAsync_ReturnsFalse_ForPayrollFormatNotInEnabledSetting()
    {
        ReturnSetting(ExportConstants.FormatBmd);

        var enabled = await _policy.IsEnabledAsync(PayrollExportConstants.FormatKeyPaxmlSe, CancellationToken.None);

        enabled.ShouldBeFalse();
    }
}
