// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Application.Interfaces.Schedules.AutoWizard;
using Klacks.Api.Application.Services.Schedules.AutoWizard;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Schedules.AutoWizard;

/// <summary>
/// The in-memory record of autofill runs in progress: a begun run is recorded before it is announced and stays recorded
/// when the announcement fails; an ended run is forgotten before its end is announced, a failing announcement never
/// escapes (the run lock is released in the same finally), and ending an unknown run announces nothing.
/// </summary>
[TestFixture]
public sealed class AutofillRunPresenceTrackerTests
{
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly DateOnly PeriodFrom = new(2026, 11, 1);
    private static readonly DateOnly PeriodUntil = new(2026, 11, 30);

    private IAutofillRunPresenceNotifier _notifier = null!;
    private AutofillRunPresenceTracker _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _notifier = Substitute.For<IAutofillRunPresenceNotifier>();
        _sut = new AutofillRunPresenceTracker(_notifier, NullLogger<AutofillRunPresenceTracker>.Instance);
    }

    [Test]
    public async Task Begin_RecordsTheRunWithoutProgress_AndAnnouncesIt()
    {
        var run = await _sut.BeginAsync(GroupId, PeriodFrom, PeriodUntil);

        run.RunId.ShouldNotBe(Guid.Empty);
        run.GroupId.ShouldBe(GroupId);
        run.PeriodFrom.ShouldBe(PeriodFrom);
        run.PeriodUntil.ShouldBe(PeriodUntil);
        run.ProgressPercent.ShouldBeNull();
        _sut.GetActive().ShouldHaveSingleItem().ShouldBe(run);
        await _notifier.Received(1).NotifyStartedAsync(run);
    }

    [Test]
    public async Task Begin_IsRecordedBeforeItIsAnnounced()
    {
        IReadOnlyList<AutofillRunPresenceDto>? activeWhenAnnounced = null;
        _notifier.When(notifier => notifier.NotifyStartedAsync(Arg.Any<AutofillRunPresenceDto>()))
            .Do(_ => activeWhenAnnounced = _sut.GetActive());

        var run = await _sut.BeginAsync(GroupId, PeriodFrom, PeriodUntil);

        activeWhenAnnounced.ShouldNotBeNull().ShouldContain(run);
    }

    [Test]
    public async Task Begin_WhenTheAnnouncementFails_StillRecordsTheRunAndDoesNotThrow()
    {
        _notifier.NotifyStartedAsync(Arg.Any<AutofillRunPresenceDto>()).ThrowsAsync(new InvalidOperationException("hub down"));

        var run = await _sut.BeginAsync(GroupId, PeriodFrom, PeriodUntil);

        _sut.GetActive().ShouldHaveSingleItem().ShouldBe(run);
    }

    [Test]
    public async Task End_ForgetsTheRunBeforeAnnouncingItsEnd()
    {
        var run = await _sut.BeginAsync(GroupId, PeriodFrom, PeriodUntil);
        IReadOnlyList<AutofillRunPresenceDto>? activeWhenAnnounced = null;
        _notifier.When(notifier => notifier.NotifyEndedAsync(Arg.Any<AutofillRunPresenceDto>()))
            .Do(_ => activeWhenAnnounced = _sut.GetActive());

        await _sut.EndAsync(run.RunId);

        _sut.GetActive().ShouldBeEmpty();
        activeWhenAnnounced.ShouldNotBeNull().ShouldBeEmpty();
        await _notifier.Received(1).NotifyEndedAsync(run);
    }

    [Test]
    public async Task End_WhenTheAnnouncementFails_StillForgetsTheRunAndDoesNotThrow()
    {
        var run = await _sut.BeginAsync(GroupId, PeriodFrom, PeriodUntil);
        _notifier.NotifyEndedAsync(Arg.Any<AutofillRunPresenceDto>()).ThrowsAsync(new InvalidOperationException("hub down"));

        await Should.NotThrowAsync(() => _sut.EndAsync(run.RunId));

        _sut.GetActive().ShouldBeEmpty();
    }

    [Test]
    public async Task End_OfAnUnknownRun_AnnouncesNothing()
    {
        await _sut.EndAsync(Guid.NewGuid());

        await _notifier.DidNotReceiveWithAnyArgs().NotifyEndedAsync(default!);
    }

    [Test]
    public async Task TwoRuns_AreRecordedSeparately_AndEndedIndependently()
    {
        var first = await _sut.BeginAsync(GroupId, PeriodFrom, PeriodUntil);
        var second = await _sut.BeginAsync(groupId: null, PeriodFrom, PeriodUntil);

        await _sut.EndAsync(first.RunId);

        first.RunId.ShouldNotBe(second.RunId);
        _sut.GetActive().ShouldHaveSingleItem().ShouldBe(second);
    }
}
