// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Application.Handlers.Exports;

using System.Text;
using Klacks.Api.Application.Commands.Exports;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Handlers.Exports;
using Klacks.Api.Application.Interfaces.Exports;
using Klacks.Api.Application.Queries.Exports;
using Klacks.Api.Application.Services.Exports;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Exports;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Infrastructure.Services.Exports;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class ExportFormatOverrideHandlerTests
{
    private IExportFormatOverrideRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IExportFormatFamilyResolver _resolver = null!;

    [OneTimeSetUp]
    public void OneTimeSetUp()
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    }

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IExportFormatOverrideRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _resolver = new ExportFormatFamilyResolver(
            [new CsvExportFormatter()],
            [new GenericDelimitedPayrollExportFormatter()],
            [new ClientPeriodCsvExportFormatter()]);
    }

    private SaveExportFormatOverrideCommandHandler CreateSaveHandler() =>
        new(_resolver, _repository, _unitOfWork);

    [Test]
    public async Task Save_rejects_unknown_format_key()
    {
        await Should.ThrowAsync<InvalidRequestException>(() =>
            CreateSaveHandler().Handle(new SaveExportFormatOverrideCommand("does-not-exist", "{}", true, null), CancellationToken.None));
    }

    [Test]
    public async Task Save_rejects_patch_with_unknown_key_for_family()
    {
        var command = new SaveExportFormatOverrideCommand("csv", """{"delimiter":","}""", true, null);

        await Should.ThrowAsync<InvalidRequestException>(() => CreateSaveHandler().Handle(command, CancellationToken.None));
    }

    [Test]
    public async Task Save_creates_row_and_stamps_version()
    {
        _repository.GetByFormatKeyAsync("generic-payroll-csv", Arg.Any<CancellationToken>())
            .Returns((ExportFormatOverride?)null);

        var result = await CreateSaveHandler().Handle(
            new SaveExportFormatOverrideCommand("generic-payroll-csv", """{"delimiter":"|"}""", true, "Ticket #123"), CancellationToken.None);

        result.FormatKey.ShouldBe("generic-payroll-csv");
        result.CreatedUnderVersion.ShouldNotBeNullOrEmpty();
        await _repository.Received(1).AddAsync(Arg.Is<ExportFormatOverride>(e => e.FormatKey == "generic-payroll-csv"), Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Save_updates_existing_row()
    {
        var existing = new ExportFormatOverride { FormatKey = "generic-payroll-csv", PatchJson = "{}", IsEnabled = false };
        _repository.GetByFormatKeyAsync("generic-payroll-csv", Arg.Any<CancellationToken>()).Returns(existing);

        await CreateSaveHandler().Handle(
            new SaveExportFormatOverrideCommand("generic-payroll-csv", """{"delimiter":"|"}""", true, null), CancellationToken.None);

        existing.PatchJson.ShouldBe("""{"delimiter":"|"}""");
        existing.IsEnabled.ShouldBeTrue();
        await _repository.DidNotReceive().AddAsync(Arg.Any<ExportFormatOverride>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Delete_returns_false_when_missing()
    {
        _repository.GetByFormatKeyAsync("csv", Arg.Any<CancellationToken>()).Returns((ExportFormatOverride?)null);

        var handler = new DeleteExportFormatOverrideCommandHandler(_repository, _unitOfWork);

        (await handler.Handle(new DeleteExportFormatOverrideCommand("csv"), CancellationToken.None)).ShouldBeFalse();
    }

    [Test]
    public async Task List_returns_catalog_with_override_and_allowed_keys()
    {
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(
            [new ExportFormatOverride { FormatKey = "generic-payroll-csv", PatchJson = """{"delimiter":"|"}""", IsEnabled = true }]);

        var handler = new ListExportFormatOverridesQueryHandler(_resolver, _repository);
        var catalog = await handler.Handle(new ListExportFormatOverridesQuery(), CancellationToken.None);

        catalog.CurrentVersion.ShouldNotBeNullOrEmpty();
        catalog.Formats.Count.ShouldBe(3);
        var payroll = catalog.Formats.Single(f => f.FormatKey == "generic-payroll-csv");
        payroll.Override.ShouldNotBeNull();
        payroll.AllowedKeys.ShouldContain(ExportOverrideConstants.KeyDelimiter);
        var clientPeriod = catalog.Formats.Single(f => f.FormatKey == "clientperiod-csv");
        clientPeriod.Override.ShouldBeNull();
        clientPeriod.AllowedKeys.ShouldContain(ExportOverrideConstants.KeyDateFormat);
    }

    [Test]
    public async Task Preview_renders_payroll_sample_with_patch_applied()
    {
        var handler = new PreviewExportFormatOverrideQueryHandler(
            _resolver,
            _repository,
            [new CsvExportFormatter()],
            [new GenericDelimitedPayrollExportFormatter()],
            [new ClientPeriodCsvExportFormatter()]);

        var result = await handler.Handle(
            new PreviewExportFormatOverrideQuery("generic-payroll-csv", """{"delimiter":"|","encoding":"utf-8"}"""), CancellationToken.None);

        result.OverrideApplied.ShouldBeTrue();
        result.FileName.ShouldBe("preview_generic-payroll-csv.csv");
        var text = Encoding.UTF8.GetString(result.FileContent);
        text.ShouldContain("|");
    }

    [Test]
    public async Task Preview_without_patch_and_without_stored_override_uses_defaults()
    {
        _repository.GetByFormatKeyAsync("csv", Arg.Any<CancellationToken>()).Returns((ExportFormatOverride?)null);

        var handler = new PreviewExportFormatOverrideQueryHandler(
            _resolver,
            _repository,
            [new CsvExportFormatter()],
            [new GenericDelimitedPayrollExportFormatter()],
            [new ClientPeriodCsvExportFormatter()]);

        var result = await handler.Handle(new PreviewExportFormatOverrideQuery("csv", null), CancellationToken.None);

        result.OverrideApplied.ShouldBeFalse();
        result.FileContent.ShouldNotBeEmpty();
    }

    [Test]
    public async Task Applier_ignores_broken_patch_and_exports_with_defaults()
    {
        _repository.GetByFormatKeyAsync("csv", Arg.Any<CancellationToken>()).Returns(
            new ExportFormatOverride { FormatKey = "csv", PatchJson = "{broken", IsEnabled = true });

        var applier = new ExportFormatOverrideApplier(_repository, NullLogger<ExportFormatOverrideApplier>.Instance);
        var options = new ExportOptions();

        (await applier.ApplyAsync("csv", options, CancellationToken.None)).ShouldBeFalse();
        options.DateFormat.ShouldBe("dd.MM.yyyy");
    }

    [Test]
    public async Task Applier_skips_disabled_override()
    {
        _repository.GetByFormatKeyAsync("csv", Arg.Any<CancellationToken>()).Returns(
            new ExportFormatOverride { FormatKey = "csv", PatchJson = """{"dateFormat":"yyyy"}""", IsEnabled = false });

        var applier = new ExportFormatOverrideApplier(_repository, NullLogger<ExportFormatOverrideApplier>.Instance);
        var options = new ExportOptions();

        (await applier.ApplyAsync("csv", options, CancellationToken.None)).ShouldBeFalse();
        options.DateFormat.ShouldBe("dd.MM.yyyy");
    }

    [Test]
    public async Task Applier_applies_enabled_override()
    {
        _repository.GetByFormatKeyAsync("csv", Arg.Any<CancellationToken>()).Returns(
            new ExportFormatOverride { FormatKey = "csv", PatchJson = """{"dateFormat":"yyyy-MM-dd"}""", IsEnabled = true });

        var applier = new ExportFormatOverrideApplier(_repository, NullLogger<ExportFormatOverrideApplier>.Instance);
        var options = new ExportOptions();

        (await applier.ApplyAsync("csv", options, CancellationToken.None)).ShouldBeTrue();
        options.DateFormat.ShouldBe("yyyy-MM-dd");
    }
}
