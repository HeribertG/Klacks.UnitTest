// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the SchedulingRule PostCommandHandler: a cap below 1 (daily hours, weekly hours, consecutive days) is
/// rejected without saving, empty caps and zero rest minimums are created (owner rule 2026-11-07).
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
public class PostCommandHandlerTests
{
    private ISchedulingRuleRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private PostCommandHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<ISchedulingRuleRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _handler = new PostCommandHandler(
            _repository,
            new ScheduleMapper(),
            _unitOfWork,
            Substitute.For<IDomainEventDispatcher>(),
            Substitute.For<ILogger<PostCommandHandler>>());
    }

    [TestCase(0, null, null)]
    [TestCase(null, 0, null)]
    [TestCase(null, null, 0)]
    public async Task Handle_CapZero_IsRejectedWithoutSaving(double? maxDailyHours, double? maxWeeklyHours, int? maxConsecutiveDays)
    {
        var resource = new SchedulingRuleResource
        {
            Name = "Rule",
            MaxDailyHours = (decimal?)maxDailyHours,
            MaxWeeklyHours = (decimal?)maxWeeklyHours,
            MaxConsecutiveDays = maxConsecutiveDays,
        };

        await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(new PostCommand<SchedulingRuleResource>(resource), CancellationToken.None));

        await _repository.DidNotReceive().Add(Arg.Any<SchedulingRule>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Handle_EmptyCapsAndZeroRestMinimums_AreCreated()
    {
        var resource = new SchedulingRuleResource
        {
            Name = "Rule",
            MaxDailyHours = null,
            MaxWeeklyHours = null,
            MaxConsecutiveDays = null,
            MinPauseHours = 0m,
            MinRestDays = 0m,
        };

        await _handler.Handle(new PostCommand<SchedulingRuleResource>(resource), CancellationToken.None);

        await _repository.Received(1).Add(Arg.Is<SchedulingRule>(r => r.MinPauseHours == 0m && r.MaxDailyHours == null));
        await _unitOfWork.Received(1).CompleteAsync();
    }
}
