// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.DTOs.Schedules.AutoWizard;
using Klacks.Api.Application.Handlers.Schedules;
using Klacks.Api.Application.Interfaces.Schedules.AutoWizard;
using Klacks.Api.Application.Queries.Schedules;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Handlers.Schedules;

/// <summary>
/// The REST view of the autofill runs in progress is filtered by the caller's group visibility, fail-closed: the scope
/// is resolved for the caller's explicit user id (none means nothing visible), a run of a hidden group is left out
/// instead of answered with an error, and a run without a group is shown to unrestricted callers only.
/// </summary>
[TestFixture]
public sealed class GetActiveAutofillRunsQueryHandlerTests
{
    private const string CallerId = "caller-1";
    private static readonly Guid VisibleGroup = Guid.NewGuid();
    private static readonly Guid HiddenGroup = Guid.NewGuid();
    private static readonly DateOnly PeriodFrom = new(2026, 11, 1);
    private static readonly DateOnly PeriodUntil = new(2026, 11, 30);
    private static readonly AutofillRunPresenceDto VisibleRun = Run(VisibleGroup);
    private static readonly AutofillRunPresenceDto HiddenRun = Run(HiddenGroup);
    private static readonly AutofillRunPresenceDto GroupLessRun = Run(null);

    private IAutofillRunPresenceTracker _tracker = null!;
    private IGroupVisibilityService _visibility = null!;
    private IUserService _userService = null!;
    private GetActiveAutofillRunsQueryHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _tracker = Substitute.For<IAutofillRunPresenceTracker>();
        _tracker.GetActive().Returns([VisibleRun, HiddenRun, GroupLessRun]);
        _visibility = Substitute.For<IGroupVisibilityService>();
        _userService = Substitute.For<IUserService>();
        _userService.GetIdString().Returns(CallerId);
        _sut = new GetActiveAutofillRunsQueryHandler(_tracker, _visibility, _userService);
    }

    [Test]
    public async Task RestrictedCaller_SeesOnlyRunsOfVisibleGroups()
    {
        _visibility.GetVisibilityScopeForUserAsync(CallerId, Arg.Any<CancellationToken>())
            .Returns(GroupVisibilityScope.Restricted([VisibleGroup], [VisibleGroup]));

        var runs = await _sut.Handle(new GetActiveAutofillRunsQuery(), CancellationToken.None);

        runs.ShouldHaveSingleItem().ShouldBe(VisibleRun);
    }

    [Test]
    public async Task UnrestrictedCaller_SeesEveryRun_IncludingGroupLessOnes()
    {
        _visibility.GetVisibilityScopeForUserAsync(CallerId, Arg.Any<CancellationToken>())
            .Returns(GroupVisibilityScope.Unrestricted());

        var runs = await _sut.Handle(new GetActiveAutofillRunsQuery(), CancellationToken.None);

        runs.ShouldBe(new[] { VisibleRun, HiddenRun, GroupLessRun }, ignoreOrder: true);
    }

    [Test]
    public async Task CallerWithoutUserId_IsResolvedFailClosed_AndSeesNothing()
    {
        _userService.GetIdString().Returns((string?)null);
        _visibility.GetVisibilityScopeForUserAsync(null, Arg.Any<CancellationToken>())
            .Returns(GroupVisibilityScope.Restricted([], []));

        var runs = await _sut.Handle(new GetActiveAutofillRunsQuery(), CancellationToken.None);

        runs.ShouldBeEmpty();
        await _visibility.DidNotReceive().GetVisibilityScopeAsync();
    }

    [Test]
    public async Task NoRunInProgress_AnswersEmpty_WithoutResolvingVisibility()
    {
        _tracker.GetActive().Returns([]);

        var runs = await _sut.Handle(new GetActiveAutofillRunsQuery(), CancellationToken.None);

        runs.ShouldBeEmpty();
        await _visibility.DidNotReceiveWithAnyArgs().GetVisibilityScopeForUserAsync(default, default);
    }

    private static AutofillRunPresenceDto Run(Guid? groupId) =>
        new(Guid.NewGuid(), groupId, PeriodFrom, PeriodUntil, ProgressPercent: null);
}
