// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for the UiAction outcome report (W1.4): a known tracking id of the caller moves the usage
/// row from Dispatched to Completed/Failed; a foreign or unknown id is reported as not found instead
/// of an error; an invalid status or a report against a non-UiAction row is a bad request.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Handlers.Assistant;

[TestFixture]
public class ReportUiActionResultCommandHandlerTests
{
    private const string UserId = "0c9f5b51-9c2d-4a2d-8a4f-4b2a2f5f9c2d";

    private ISkillUsageRepository _repository = null!;
    private ReportUiActionResultCommandHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ISkillUsageRepository>();
        _handler = new ReportUiActionResultCommandHandler(
            _repository, Substitute.For<ILogger<ReportUiActionResultCommandHandler>>());
    }

    [Test]
    public async Task KnownDispatch_ReportedCompleted_SetsSuccessAndStatus()
    {
        var record = GivenDispatch();

        var result = await _handler.Handle(Command(record.Id, "completed"), CancellationToken.None);

        result.Found.ShouldBeTrue();
        result.Updated.ShouldBeTrue();
        record.UiActionStatus.ShouldBe(UiActionStatus.Completed);
        record.Success.ShouldBeTrue();
        record.ErrorMessage.ShouldBeNull();
        await _repository.Received(1).UpdateAsync(record, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task KnownDispatch_ReportedFailed_SetsFailureAndError()
    {
        var record = GivenDispatch();

        var result = await _handler.Handle(Command(record.Id, "failed", "Browser: popup blocked"), CancellationToken.None);

        result.Found.ShouldBeTrue();
        result.Updated.ShouldBeTrue();
        record.UiActionStatus.ShouldBe(UiActionStatus.Failed);
        record.Success.ShouldBeFalse();
        record.ErrorMessage.ShouldBe("Browser: popup blocked");
        await _repository.Received(1).UpdateAsync(record, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownOrForeignId_IsReportedAsNotFound()
    {
        GivenDispatch(); // a row of a different user

        var result = await _handler.Handle(Command(Guid.NewGuid(), "completed"), CancellationToken.None);

        result.Found.ShouldBeFalse();
        result.Updated.ShouldBeFalse();
        await _repository.DidNotReceive().UpdateAsync(Arg.Any<SkillUsageRecord>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReportAgainstNonUiActionRow_ThrowsBadRequest()
    {
        var record = GivenDispatch();
        record.UiActionStatus = null;

        await Should.ThrowAsync<ArgumentException>(() =>
            _handler.Handle(Command(record.Id, "completed"), CancellationToken.None));
    }

    [Test]
    public async Task UnknownStatus_ThrowsBadRequest()
    {
        var record = GivenDispatch();

        await Should.ThrowAsync<ArgumentException>(() =>
            _handler.Handle(Command(record.Id, "maybe"), CancellationToken.None));
    }

    private SkillUsageRecord GivenDispatch()
    {
        var record = new SkillUsageRecord
        {
            Id = Guid.NewGuid(),
            SkillName = "open_settings",
            Category = SkillCategory.Action,
            UserId = Guid.Parse(UserId),
            TenantId = Guid.NewGuid(),
            Success = true,
            UiActionStatus = UiActionStatus.Dispatched,
            Timestamp = DateTime.UtcNow
        };
        _repository.GetByIdAsync(record.Id, Arg.Any<CancellationToken>()).Returns(record);
        return record;
    }

    private static ReportUiActionResultCommand Command(Guid trackingId, string status, string? errorMessage = null) => new()
    {
        UserId = UserId,
        TrackingId = trackingId,
        Status = status,
        ErrorMessage = errorMessage
    };
}
