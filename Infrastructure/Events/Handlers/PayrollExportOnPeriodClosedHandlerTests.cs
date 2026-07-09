// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the DATEV payroll-export country-pack hook: feature gating, group-scope gating,
/// idempotency and the happy-path (load, format, store, log).
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Exports;
using Klacks.Api.Application.Interfaces.Plugins;
using Klacks.Api.Application.Queries.Exports;
using Klacks.Api.Domain.Events;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Exports;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Exports;
using Klacks.Api.Domain.Models.Exports.Payroll;
using Klacks.Api.Infrastructure.Events.Handlers;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Infrastructure.Events.Handlers;

[TestFixture]
public class PayrollExportOnPeriodClosedHandlerTests
{
    private static readonly Guid GroupId = Guid.NewGuid();

    private IFeaturePluginService _featurePluginService = null!;
    private IMediator _mediator = null!;
    private IPayrollExportConfigRepository _configRepository = null!;
    private IPayrollExportFormatter _formatter = null!;
    private IObjectStorageService _objectStorage = null!;
    private IExportLogRepository _exportLogRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ILogger<PayrollExportOnPeriodClosedHandler> _logger = null!;
    private PayrollExportOnPeriodClosedHandler _handler = null!;

    private static PeriodClosedEvent GroupEvent() => new(
        new DateOnly(2026, 1, 1),
        new DateOnly(2026, 1, 31),
        GroupId,
        10,
        3,
        31,
        "admin-user");

    private static PeriodClosedEvent FullPeriodEvent() => new(
        new DateOnly(2026, 1, 1),
        new DateOnly(2026, 1, 31),
        null,
        10,
        3,
        31,
        "admin-user");

    [SetUp]
    public void Setup()
    {
        _featurePluginService = Substitute.For<IFeaturePluginService>();
        _mediator = Substitute.For<IMediator>();
        _configRepository = Substitute.For<IPayrollExportConfigRepository>();
        _formatter = Substitute.For<IPayrollExportFormatter>();
        _objectStorage = Substitute.For<IObjectStorageService>();
        _exportLogRepository = Substitute.For<IExportLogRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _logger = Substitute.For<ILogger<PayrollExportOnPeriodClosedHandler>>();

        _formatter.FormatKey.Returns(PayrollExportConstants.FormatKeyDatevLug);
        _formatter.FileExtension.Returns(PayrollExportConstants.FileExtensionCsv);
        _configRepository.GetByGroupAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new PayrollExportGroupConfig { GroupId = GroupId });
        _exportLogRepository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<ExportLog>());

        _handler = new PayrollExportOnPeriodClosedHandler(
            _featurePluginService,
            _mediator,
            _configRepository,
            [_formatter],
            _objectStorage,
            _exportLogRepository,
            _unitOfWork,
            _logger);
    }

    private void EnableFeature() =>
        _featurePluginService.IsEnabled(PayrollExportOnPeriodClosedHandler.FeaturePluginName).Returns(true);

    private void ReturnData(PayrollExportData data) =>
        _mediator.Send(Arg.Any<GetPayrollPeriodDataQuery>(), Arg.Any<CancellationToken>()).Returns(data);

    private static PayrollExportData SampleData() => new()
    {
        GroupId = GroupId,
        StartDate = new DateOnly(2026, 1, 1),
        EndDate = new DateOnly(2026, 1, 31),
        Employees =
        [
            new PayrollEmployee { ClientId = Guid.NewGuid(), IdNumber = 1, FullName = "A" },
        ],
    };

    [Test]
    public async Task HandleAsync_DoesNothing_WhenFeatureDisabled()
    {
        _featurePluginService.IsEnabled(PayrollExportOnPeriodClosedHandler.FeaturePluginName).Returns(false);

        await _handler.HandleAsync(GroupEvent(), CancellationToken.None);

        await _mediator.DidNotReceive().Send(Arg.Any<GetPayrollPeriodDataQuery>(), Arg.Any<CancellationToken>());
        await _objectStorage.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_Skips_WhenNoGroupScope()
    {
        EnableFeature();

        await _handler.HandleAsync(FullPeriodEvent(), CancellationToken.None);

        await _mediator.DidNotReceive().Send(Arg.Any<GetPayrollPeriodDataQuery>(), Arg.Any<CancellationToken>());
        await _objectStorage.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_Skips_WhenAlreadyExported()
    {
        EnableFeature();
        _exportLogRepository.GetRangeAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<ExportLog>
            {
                new()
                {
                    GroupId = GroupId,
                    Format = PayrollExportConstants.TargetSystemDatevLug,
                    StartDate = new DateOnly(2026, 1, 1),
                    EndDate = new DateOnly(2026, 1, 31),
                },
            });

        await _handler.HandleAsync(GroupEvent(), CancellationToken.None);

        await _mediator.DidNotReceive().Send(Arg.Any<GetPayrollPeriodDataQuery>(), Arg.Any<CancellationToken>());
        await _objectStorage.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_DoesNotExport_WhenNoEmployees()
    {
        EnableFeature();
        ReturnData(new PayrollExportData { GroupId = GroupId });

        await _handler.HandleAsync(GroupEvent(), CancellationToken.None);

        _formatter.DidNotReceive().Format(Arg.Any<PayrollExportData>(), Arg.Any<PayrollExportGroupConfig>());
        await _objectStorage.DidNotReceive().UploadAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HandleAsync_FormatsStoresAndLogs_OnHappyPath()
    {
        EnableFeature();
        ReturnData(SampleData());
        _formatter.Format(Arg.Any<PayrollExportData>(), Arg.Any<PayrollExportGroupConfig>())
            .Returns(new PayrollExportResult { Content = [1, 2, 3], RecordCount = 1, SkippedAbsenceCount = 0 });

        await _handler.HandleAsync(GroupEvent(), CancellationToken.None);

        _formatter.Received(1).Format(Arg.Any<PayrollExportData>(), Arg.Any<PayrollExportGroupConfig>());
        await _objectStorage.Received(1).UploadAsync(Arg.Any<string>(), Arg.Any<Stream>(), Arg.Any<CancellationToken>());
        await _exportLogRepository.Received(1).AddAsync(
            Arg.Is<ExportLog>(l => l.GroupId == GroupId
                && l.Format == PayrollExportConstants.TargetSystemDatevLug
                && l.RecordCount == 1
                && l.FileSize == 3),
            Arg.Any<CancellationToken>());
        await _unitOfWork.Received(1).CompleteAsync();
    }
}
