// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the SchedulingRule PutCommandHandler: a committed update of surcharge-relevant fields
/// dispatches a SchedulingRuleChangedEvent; a rename or planning-only change never dispatches; a cap below 1
/// (daily hours, weekly hours, consecutive days) is rejected, empty caps and zero rest minimums are accepted.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Scheduling;
using Klacks.Api.Application.Handlers.SchedulingRules;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Events;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Scheduling;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.SchedulingRules;

[TestFixture]
public class PutCommandHandlerTests
{
    private ISchedulingRuleRepository _repository = null!;
    private ScheduleMapper _mapper = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IDomainEventDispatcher _eventDispatcher = null!;
    private PutCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<ISchedulingRuleRepository>();
        _mapper = new ScheduleMapper();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _eventDispatcher = Substitute.For<IDomainEventDispatcher>();

        _handler = new PutCommandHandler(
            _repository,
            _mapper,
            _unitOfWork,
            _eventDispatcher,
            Substitute.For<ILogger<PutCommandHandler>>());
    }

    [Test]
    public async Task Handle_NightRateChanged_DispatchesSchedulingRuleChangedEvent()
    {
        var ruleId = Guid.NewGuid();
        _repository.Get(ruleId).Returns(new SchedulingRule { Id = ruleId, Name = "Rule", NightRate = 0.10m });

        var resource = new SchedulingRuleResource { Id = ruleId, Name = "Rule", NightRate = 0.25m };

        await _handler.Handle(new PutCommand<SchedulingRuleResource>(resource), CancellationToken.None);

        await _unitOfWork.Received(1).CompleteAsync();
        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Is<IDomainEvent>(e => e is SchedulingRuleChangedEvent && ((SchedulingRuleChangedEvent)e).RuleId == ruleId),
            Arg.Any<CancellationToken>());
    }

    [TestCase(-1)]
    [TestCase(24.5)]
    public async Task Handle_DailyFrameOutsideZeroTo24Hours_IsRejectedWithoutSaving(decimal hours)
    {
        var resource = new SchedulingRuleResource { Id = Guid.NewGuid(), Name = "Rule", MaxDailySpanHours = hours };

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PutCommand<SchedulingRuleResource>(resource), CancellationToken.None));

        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [TestCase(0, null, null)]
    [TestCase(null, 0, null)]
    [TestCase(null, null, 0)]
    [TestCase(0.5, null, null)]
    public async Task Handle_CapZeroOrBelowOne_IsRejectedWithoutSaving(double? maxDailyHours, double? maxWeeklyHours, int? maxConsecutiveDays)
    {
        var resource = new SchedulingRuleResource
        {
            Id = Guid.NewGuid(),
            Name = "Rule",
            MaxDailyHours = (decimal?)maxDailyHours,
            MaxWeeklyHours = (decimal?)maxWeeklyHours,
            MaxConsecutiveDays = maxConsecutiveDays,
        };

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PutCommand<SchedulingRuleResource>(resource), CancellationToken.None));

        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_EmptyCapsAndZeroRestMinimums_AreAccepted()
    {
        var ruleId = Guid.NewGuid();
        var existing = new SchedulingRule { Id = ruleId, Name = "Rule", MaxDailyHours = 10m, MinPauseHours = 11m };
        _repository.Get(ruleId).Returns(existing);

        var resource = new SchedulingRuleResource
        {
            Id = ruleId,
            Name = "Rule",
            MaxDailyHours = null,
            MaxWeeklyHours = 1m,
            MaxConsecutiveDays = 1,
            MinPauseHours = 0m,
            MinRestDays = 0m,
        };

        await _handler.Handle(new PutCommand<SchedulingRuleResource>(resource), CancellationToken.None);

        await _unitOfWork.Received(1).CompleteAsync();
        existing.MaxDailyHours.ShouldBeNull();
        existing.MinPauseHours.ShouldBe(0m);
    }

    [Test]
    public async Task Handle_PlanningOnlyChange_DoesNotDispatch()
    {
        var ruleId = Guid.NewGuid();
        _repository.Get(ruleId).Returns(new SchedulingRule { Id = ruleId, Name = "Old", NightRate = 0.10m, MaxWorkDays = 5 });

        var resource = new SchedulingRuleResource { Id = ruleId, Name = "New", NightRate = 0.10m, MaxWorkDays = 6 };

        await _handler.Handle(new PutCommand<SchedulingRuleResource>(resource), CancellationToken.None);

        await _unitOfWork.Received(1).CompleteAsync();
        await _eventDispatcher.DidNotReceive().DispatchAsync(Arg.Any<IDomainEvent>(), Arg.Any<CancellationToken>());
    }

    /// <summary>
    /// Industry and the import keys are import identity, never editable. If an edit through the
    /// settings card could clear them, the rule would silently drop out of the industry filter and
    /// the next region import would treat it as a new row.
    /// </summary>
    [Test]
    public async Task Handle_EditThroughTheCard_LeavesImportIdentityUntouched()
    {
        var ruleId = Guid.NewGuid();
        var existing = new SchedulingRule
        {
            Id = ruleId,
            Name = "Preset",
            Industry = "healthcare",
            ImportSourceKey = "region-setup:industryProfiles:healthcare:Preset",
            ImportContentHash = "hash-before",
            MaxWorkDays = 5,
        };
        _repository.Get(ruleId).Returns(existing);

        var resource = new SchedulingRuleResource
        {
            Id = ruleId,
            Name = "Preset renamed by admin",
            Industry = string.Empty,
            ImportSourceKey = string.Empty,
            MaxWorkDays = 6,
        };

        await _handler.Handle(new PutCommand<SchedulingRuleResource>(resource), CancellationToken.None);

        existing.Industry.ShouldBe("healthcare");
        existing.ImportSourceKey.ShouldBe("region-setup:industryProfiles:healthcare:Preset");
        existing.ImportContentHash.ShouldBe("hash-before");
        existing.MaxWorkDays.ShouldBe(6, "editable planning fields must still be applied");
    }

    [Test]
    public void CreateMapping_NeverAcceptsImportIdentityFromTheClient()
    {
        var resource = new SchedulingRuleResource
        {
            Name = "Customer rule",
            Industry = "healthcare",
            ImportSourceKey = "region-setup:industryProfiles:healthcare:Spoofed",
        };

        var entity = _mapper.ToSchedulingRuleEntity(resource);

        entity.Industry.ShouldBeEmpty("a rule created through the API is customer-owned");
        entity.ImportSourceKey.ShouldBeEmpty();
        entity.ImportContentHash.ShouldBeEmpty();
    }

    [Test]
    public async Task Handle_PerformsShiftWorkChanged_DispatchesSchedulingRuleChangedEvent()
    {
        var ruleId = Guid.NewGuid();
        _repository.Get(ruleId).Returns(new SchedulingRule { Id = ruleId, Name = "Rule", NightRate = 0.10m, PerformsShiftWork = null });

        var resource = new SchedulingRuleResource { Id = ruleId, Name = "Rule", NightRate = 0.10m, PerformsShiftWork = false };

        await _handler.Handle(new PutCommand<SchedulingRuleResource>(resource), CancellationToken.None);

        await _eventDispatcher.Received(1).DispatchAsync(
            Arg.Is<IDomainEvent>(e => e is SchedulingRuleChangedEvent && ((SchedulingRuleChangedEvent)e).RuleId == ruleId),
            Arg.Any<CancellationToken>());
    }
}
