// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// GetPeriodIssuesQueryHandler and services on stand-ins: the handler adds one Error entry with the count of
/// IPeriodSealCompletenessGuard.CountStandInWorksAsync for the same period and group, so the issues card shows it; the error
/// count the automatic close and the close advice read (PeriodCloseGates.CountErrors) leaves it out, because both refuse on
/// stand-ins on their own before the errors and no acknowledgement lifts that; without stand-in services nothing is added.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Handlers.PeriodClosing;
using Klacks.Api.Application.Interfaces.PeriodClosing;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Queries.PeriodClosing;
using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Application.Handlers.PeriodClosing;

[TestFixture]
public class GetPeriodIssuesQueryHandlerStandInTests
{
    private static readonly DateOnly From = new(2026, 11, 1);
    private static readonly DateOnly To = new(2026, 11, 30);
    private static readonly Guid GroupId = Guid.NewGuid();

    private IPeriodSealCompletenessGuard _sealGuard = null!;
    private GetPeriodIssuesQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        var readRepository = Substitute.For<IPeriodClosingReadRepository>();
        readRepository.GetScheduleNoteIssues(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<PeriodScheduleNoteIssueRow>());
        var validationLoader = Substitute.For<IPeriodValidationLoader>();
        validationLoader.LoadAsync(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(), Arg.Any<int?>(), Arg.Any<CancellationToken>())
            .Returns(_ => new List<PeriodIssueDto>());
        _sealGuard = Substitute.For<IPeriodSealCompletenessGuard>();

        _handler = new GetPeriodIssuesQueryHandler(
            readRepository, validationLoader, Substitute.For<IComplianceEscalationService>(),
            TestGroupWriteVisibility.UnrestrictedGroups(), new HiddenClientsVisibilityGuard(), _sealGuard,
            NullLogger<GetPeriodIssuesQueryHandler>.Instance);
    }

    [Test]
    public async Task StandInWorks_AddOneErrorEntry_WithTheGuardCount_ForTheSamePeriodAndGroup()
    {
        _sealGuard.CountStandInWorksAsync(From, To, GroupId, Arg.Any<CancellationToken>()).Returns(3);

        var issues = await _handler.Handle(new GetPeriodIssuesQuery(From, To, GroupId), CancellationToken.None);

        var entry = issues.ShouldHaveSingleItem();
        entry.Code.ShouldBe(PeriodIssueLimits.StandInWorksCode);
        entry.Severity.ShouldBe(ScheduleValidationType.Error);
        entry.ClientId.ShouldBe(Guid.Empty);
        entry.Date.ShouldBe(From);
        entry.MessageParams["count"].ShouldBe("3");
        PeriodCloseGates.CountErrors(issues).ShouldBe(0);
    }

    [Test]
    public async Task NoStandInWorks_AddNothing()
    {
        _sealGuard.CountStandInWorksAsync(From, To, null, Arg.Any<CancellationToken>()).Returns(0);

        var issues = await _handler.Handle(new GetPeriodIssuesQuery(From, To, null), CancellationToken.None);

        issues.ShouldBeEmpty();
        await _sealGuard.Received(1).CountStandInWorksAsync(From, To, null, Arg.Any<CancellationToken>());
    }
}
