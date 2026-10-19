// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for get_erp_import_status: reports the drop point's enabled state, cron schedule, the file
/// counts per state from the erp_import_file ledger (with the stale cutoff passed through) and a summary of
/// the latest finished file, and formats the next scheduled run from the persisted setting.
/// </summary>

using Klacks.Api.Application.DTOs.ErpDropPoints;
using Klacks.Api.Application.Queries.ErpDropPoints;
using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Imports;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;
using SettingsModel = Klacks.Api.Domain.Models.Settings.Settings;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetErpImportStatusSkillTests
{
    private static readonly DateTime Now = new(2026, 10, 19, 8, 0, 0, DateTimeKind.Utc);
    private static readonly Guid DropPointId = Guid.NewGuid();

    private IMediator _mediator = null!;
    private IErpImportFileRepository _fileRepository = null!;
    private ISettingsReader _settingsReader = null!;

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Roles.Admin }
    };

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _fileRepository = Substitute.For<IErpImportFileRepository>();
        _settingsReader = Substitute.For<ISettingsReader>();
        _fileRepository.CountByStatusAsync(Arg.Any<Guid>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new ErpImportFileStatusCounts(0, 0, 0, 0));
        _fileRepository.GetLatestFinishedAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((ErpImportFile?)null);
    }

    [Test]
    public async Task Disabled_ReportsDisabled_WithoutScheduleDetails()
    {
        // Arrange
        GivenDropPoint(isEnabled: false);
        var skill = CreateSkill(new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc));

        // Act
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        // Assert
        result.Success.ShouldBeTrue();
        result.Message.ShouldStartWith("ERP import is currently disabled.");
        result.Message.ShouldNotContain("schedule");
    }

    [Test]
    public async Task Enabled_ReportsSchedule_NextRun_AndLedgerCountsWithTheStaleCutoff()
    {
        // Arrange
        GivenDropPoint(isEnabled: true);
        _fileRepository.CountByStatusAsync(DropPointId, Now - ErpImportFileTiming.StaleAfter, Arg.Any<CancellationToken>())
            .Returns(new ErpImportFileStatusCounts(Received: 1, Importing: 2, Processed: 250, Failed: 3));
        _settingsReader.GetSetting(ErpImportSettingsTypes.CronExpression)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronExpression, Value = "0 * * * *" });
        _settingsReader.GetSetting(ErpImportSettingsTypes.CronTimeZoneId)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.CronTimeZoneId, Value = "Europe/Zurich" });
        _settingsReader.GetSetting(ErpImportSettingsTypes.NextRunUtc)
            .Returns(new SettingsModel { Type = ErpImportSettingsTypes.NextRunUtc, Value = new DateTime(2026, 7, 4, 10, 0, 0, DateTimeKind.Utc).ToString("O") });
        var skill = CreateSkill(new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc));

        // Act
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        // Assert
        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("enabled");
        result.Message.ShouldContain("1 waiting, 2 importing, 250 processed, 3 failed", Case.Sensitive,
            "the counts come from the uncapped ledger, not from the 200-row listing");
        await _mediator.DidNotReceive().Send(Arg.Any<GetDefaultFilesQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task LatestFinishedFileWithWarnings_IsSummarisedWithItsCounts()
    {
        // Arrange
        GivenDropPoint(isEnabled: true);
        _fileRepository.GetLatestFinishedAsync(DropPointId, Arg.Any<CancellationToken>()).Returns(new ErpImportFile
        {
            Id = Guid.NewGuid(),
            FileName = "orders.xml",
            Status = ErpImportFileStatus.CompletedWithWarnings,
            FinishedAt = Now.AddMinutes(-3),
            TotalOrders = 10,
            NewCount = 4,
            UpdatedCount = 3,
            UnchangedCount = 1,
            SupersededCount = 1,
            RejectedCount = 1
        });
        var skill = CreateSkill(new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc));

        // Act
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        // Assert
        result.Message.ShouldContain("'orders.xml' CompletedWithWarnings");
        result.Message.ShouldContain("4 new, 3 updated, 1 unchanged, 1 superseded, 1 rejected");
    }

    [Test]
    public async Task LatestFinishedFileFailed_IsSummarisedWithItsReason()
    {
        // Arrange
        GivenDropPoint(isEnabled: true);
        _fileRepository.GetLatestFinishedAsync(DropPointId, Arg.Any<CancellationToken>()).Returns(new ErpImportFile
        {
            Id = Guid.NewGuid(),
            FileName = "broken.xml",
            Status = ErpImportFileStatus.Failed,
            FinishedAt = Now.AddMinutes(-3),
            FailureReason = ErpImportFileFailureReasons.FileChangedAfterReceipt
        });
        var skill = CreateSkill(new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.Utc));

        // Act
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        // Assert
        result.Message.ShouldContain("'broken.xml' Failed");
        result.Message.ShouldContain(ErpImportFileFailureReasons.FileChangedAfterReceipt);
    }

    [Test]
    public async Task MissingCronSettings_FallBackToDefaultCronAndCompanyZone()
    {
        // Arrange
        GivenDropPoint(isEnabled: true);
        var skill = CreateSkill(new FixedCompanyClock(DateTimeOffset.UtcNow, TimeZoneInfo.FindSystemTimeZoneById("Asia/Tokyo")));

        // Act
        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        // Assert
        result.Success.ShouldBeTrue();
        result.Message.ShouldContain(ErpImportSettingsTypes.DefaultCronExpression);
        result.Message.ShouldContain("Asia/Tokyo");
    }

    private void GivenDropPoint(bool isEnabled)
    {
        _mediator.Send(Arg.Any<GetDefaultQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ErpDropPointResource { Id = DropPointId, IsEnabled = isEnabled });
    }

    private GetErpImportStatusSkill CreateSkill(FixedCompanyClock companyClock)
    {
        return new GetErpImportStatusSkill(
            _mediator,
            _fileRepository,
            new SettableTimeProvider(Now),
            _settingsReader,
            companyClock,
            NullLogger<GetErpImportStatusSkill>.Instance,
            new ErpCronTimeZoneDriftNotifier());
    }
}
