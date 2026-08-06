// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Notifications;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Schedules;
using Klacks.Api.Infrastructure.Services.Schedules;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules;

/// <summary>
/// Nobody asked for a background candidate, so it must not accumulate. Retiring one has to remove its
/// cloned schedule data as well - a scenario row whose data is gone would open as an empty plan - and
/// the push telling the open scenario list is best effort: the candidate IS retired either way.
/// </summary>
[TestFixture]
public sealed class Wizard4CandidateLifecycleServiceTests
{
    private static readonly DateOnly From = new(2026, 8, 1);
    private static readonly DateOnly Until = new(2026, 8, 31);

    private IAnalyseScenarioService _scenarioService = null!;
    private IAnalyseScenarioRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IWorkNotificationService _notificationService = null!;
    private Wizard4CandidateLifecycleService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _scenarioService = Substitute.For<IAnalyseScenarioService>();
        _repository = Substitute.For<IAnalyseScenarioRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _notificationService = Substitute.For<IWorkNotificationService>();
        _sut = new Wizard4CandidateLifecycleService(
            _scenarioService,
            _repository,
            _unitOfWork,
            _notificationService,
            NullLogger<Wizard4CandidateLifecycleService>.Instance);
    }

    [Test]
    public async Task SupersedeAsync_RemovesTheClonedDataBeforeMarkingTheScenario()
    {
        var candidate = Candidate();

        await _sut.SupersedeAsync(candidate, CancellationToken.None);

        await _scenarioService.Received(1).SoftDeleteScenarioDataAsync(candidate.Token, Arg.Any<CancellationToken>());
        candidate.Status.ShouldBe(AnalyseScenarioStatus.Rejected);
        await _repository.Received(1).Put(candidate);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task SupersedeAsync_PushesTheSupersededKind()
    {
        var candidate = Candidate();

        await _sut.SupersedeAsync(candidate, CancellationToken.None);

        await _notificationService.Received(1).NotifyWizard4CandidatesChanged(
            Arg.Is<Wizard4CandidateNotificationDto>(n =>
                n.ScenarioId == candidate.Id
                && n.ChangeKind == Wizard4LifecycleConstants.ChangeKindSuperseded));
    }

    [Test]
    public async Task SupersedeAsync_FailedPush_StillRetiresTheCandidate()
    {
        var candidate = Candidate();
        _notificationService
            .NotifyWizard4CandidatesChanged(Arg.Any<Wizard4CandidateNotificationDto>())
            .Returns<Task>(_ => throw new InvalidOperationException("hub down"));

        await Should.NotThrowAsync(() => _sut.SupersedeAsync(candidate, CancellationToken.None));

        candidate.Status.ShouldBe(AnalyseScenarioStatus.Rejected);
    }

    [Test]
    public async Task ExpireStaleCandidatesAsync_RetiresEveryStaleCandidate()
    {
        var first = Candidate();
        var second = Candidate();
        _repository
            .GetStaleCandidatesAsync(Wizard4LifecycleConstants.SystemActor, Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([first, second]);

        var expired = await _sut.ExpireStaleCandidatesAsync(new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc), CancellationToken.None);

        expired.ShouldBe(2);
        first.Status.ShouldBe(AnalyseScenarioStatus.Rejected);
        second.Status.ShouldBe(AnalyseScenarioStatus.Rejected);
    }

    [Test]
    public async Task ExpireStaleCandidatesAsync_UsesTheTimeToLiveAsCutoff()
    {
        var now = new DateTime(2026, 9, 1, 12, 0, 0, DateTimeKind.Utc);
        _repository
            .GetStaleCandidatesAsync(Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([]);

        await _sut.ExpireStaleCandidatesAsync(now, CancellationToken.None);

        await _repository.Received(1).GetStaleCandidatesAsync(
            Wizard4LifecycleConstants.SystemActor,
            now - Wizard4LifecycleConstants.CandidateTtl,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExpireStaleCandidatesAsync_NothingStale_TouchesNothing()
    {
        _repository
            .GetStaleCandidatesAsync(Arg.Any<string>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns([]);

        var expired = await _sut.ExpireStaleCandidatesAsync(DateTime.UtcNow, CancellationToken.None);

        expired.ShouldBe(0);
        await _scenarioService.DidNotReceiveWithAnyArgs().SoftDeleteScenarioDataAsync(default, default);
        await _notificationService.DidNotReceiveWithAnyArgs().NotifyWizard4CandidatesChanged(default!);
    }

    private static AnalyseScenario Candidate() => new()
    {
        Id = Guid.NewGuid(),
        Token = Guid.NewGuid(),
        GroupId = Guid.NewGuid(),
        FromDate = From,
        UntilDate = Until,
        CreatedByUser = Wizard4LifecycleConstants.SystemActor,
        Status = AnalyseScenarioStatus.Active,
    };
}
