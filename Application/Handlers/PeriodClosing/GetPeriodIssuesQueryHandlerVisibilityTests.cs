// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// GetPeriodIssuesQueryHandler is bounded by the caller's group visibility (defense in depth behind the Admin-only
/// endpoint and skill gate): an unrestricted caller sees every finding, a restricted caller gets nothing for a hidden
/// group, loses the findings of hidden clients (schedule notes included) and keeps client-less findings only when a
/// visible group was selected.
/// </summary>

using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Handlers.PeriodClosing;
using Klacks.Api.Application.Interfaces.PeriodClosing;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Queries.PeriodClosing;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Handlers.PeriodClosing;

[TestFixture]
public class GetPeriodIssuesQueryHandlerVisibilityTests
{
    private static readonly DateOnly From = new(2026, 6, 1);
    private static readonly DateOnly To = new(2026, 6, 30);
    private static readonly Guid GroupId = Guid.NewGuid();
    private static readonly Guid VisibleClientId = Guid.NewGuid();
    private static readonly Guid HiddenClientId = Guid.NewGuid();

    private IPeriodClosingReadRepository _readRepository = null!;
    private IPeriodValidationLoader _validationLoader = null!;

    [SetUp]
    public void Setup()
    {
        _readRepository = Substitute.For<IPeriodClosingReadRepository>();
        _readRepository.GetScheduleNoteIssues(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<PeriodScheduleNoteIssueRow>
            {
                new(From, HiddenClientId, "Eva", "Hidden", "private note")
            });

        _validationLoader = Substitute.For<IPeriodValidationLoader>();
        _validationLoader.LoadAsync(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new List<PeriodIssueDto>
            {
                Finding(VisibleClientId, "Anna"),
                Finding(HiddenClientId, "Eva"),
                Finding(Guid.Empty, string.Empty)
            });
    }

    private static PeriodIssueDto Finding(Guid clientId, string clientName) => new()
    {
        Date = From,
        ClientId = clientId,
        ClientName = clientName,
        Severity = ScheduleValidationType.Error,
        Code = "Collision",
        MessageKey = "schedule.error-list.collision",
        MessageParams = new Dictionary<string, string>()
    };

    private GetPeriodIssuesQueryHandler Handler(IGroupVisibilityGuard groupGuard, IClientVisibilityGuard clientGuard)
        => new(_readRepository, _validationLoader, Substitute.For<IComplianceEscalationService>(),
            groupGuard, clientGuard, NullLogger<GetPeriodIssuesQueryHandler>.Instance);

    private static IGroupVisibilityGuard RestrictedTo(params Guid[] visibleGroupIds)
    {
        var visible = visibleGroupIds.ToHashSet();
        var guard = Substitute.For<IGroupVisibilityGuard>();
        guard.IsUnrestrictedAsync(Arg.Any<CancellationToken>()).Returns(false);
        guard.IsGroupVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => Task.FromResult(visible.Contains(ci.Arg<Guid>())));
        return guard;
    }

    [Test]
    public async Task UnrestrictedCaller_SeesEveryFinding()
    {
        var handler = Handler(TestGroupWriteVisibility.UnrestrictedGroups(), new HiddenClientsVisibilityGuard(HiddenClientId));

        var issues = await handler.Handle(new GetPeriodIssuesQuery(From, To, null), CancellationToken.None);

        issues.Count.ShouldBe(4);
    }

    [Test]
    public async Task RestrictedCaller_HiddenGroup_GetsNothing_AndNothingIsLoaded()
    {
        var handler = Handler(RestrictedTo(), new HiddenClientsVisibilityGuard());

        var issues = await handler.Handle(new GetPeriodIssuesQuery(From, To, GroupId), CancellationToken.None);

        issues.ShouldBeEmpty();
        await _validationLoader.DidNotReceive().LoadAsync(
            Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RestrictedCaller_VisibleGroup_DropsHiddenClients_KeepsClientlessFindings()
    {
        var handler = Handler(RestrictedTo(GroupId), new HiddenClientsVisibilityGuard(HiddenClientId));

        var issues = await handler.Handle(new GetPeriodIssuesQuery(From, To, GroupId), CancellationToken.None);

        issues.Select(i => i.ClientId).ShouldBe(new[] { VisibleClientId, Guid.Empty }, ignoreOrder: true);
    }

    [Test]
    public async Task RestrictedCaller_WithoutGroup_DropsHiddenClients_AndClientlessFindings()
    {
        var handler = Handler(RestrictedTo(GroupId), new HiddenClientsVisibilityGuard(HiddenClientId));

        var issues = await handler.Handle(new GetPeriodIssuesQuery(From, To, null), CancellationToken.None);

        issues.ShouldHaveSingleItem().ClientId.ShouldBe(VisibleClientId);
    }
}
