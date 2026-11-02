// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The preview of a bulk absence names what would fall, writes nothing and refuses people the caller may not see.
/// </summary>

using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Handlers.Breaks;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Queries.Breaks;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Handlers.Breaks;

[TestFixture]
public class PreviewTakenWorksQueryHandlerTests
{
    private static readonly DateOnly Day = new(2026, 11, 10);
    private static readonly Guid Elsa = Guid.NewGuid();

    private IScheduleRowAccessGuard _access = null!;
    private IAbsenceWorkRemovalService _removal = null!;
    private PreviewTakenWorksQueryHandler _handler = null!;
    private BulkAddBreaksRequest _request = null!;

    [SetUp]
    public void SetUp()
    {
        _access = Substitute.For<IScheduleRowAccessGuard>();
        _access.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(true);
        _removal = Substitute.For<IAbsenceWorkRemovalService>();
        _handler = new PreviewTakenWorksQueryHandler(_access, _removal);
        _request = new BulkAddBreaksRequest
        {
            Breaks = [new BulkBreakItem { ClientId = Elsa, AbsenceId = Guid.NewGuid(), CurrentDate = Day }]
        };
    }

    [Test]
    public async Task NamesTheWorks_AndRemovesNothing()
    {
        var work = new RemovableWork(Guid.NewGuid(), Elsa, Guid.NewGuid(), Day, new TimeOnly(8, 0), new TimeOnly(16, 0));
        _removal.FindAsync(Arg.Any<IReadOnlyList<AbsenceSpan>>(), null, Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<RemovableWork>)[work]);

        var result = await _handler.Handle(new PreviewTakenWorksQuery(_request), CancellationToken.None);

        result.ShouldHaveSingleItem().ShouldBe(work);
        await _removal.DidNotReceiveWithAnyArgs().WriteAndRemoveAsync(default!, default!, default);
    }

    [Test]
    public async Task TooManyAbsences_AreRefused()
    {
        _request.Breaks = Enumerable.Range(0, RateLimitingPolicies.MaxBulkOperationItems + 1)
            .Select(_ => _request.Breaks[0])
            .ToList();

        await Should.ThrowAsync<ArgumentException>(() => _handler.Handle(new PreviewTakenWorksQuery(_request), CancellationToken.None));

        await _removal.DidNotReceiveWithAnyArgs().FindAsync(default!, default, default);
    }

    [Test]
    public async Task APersonOutOfSight_IsRefused()
    {
        _access.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Guid?>(), Arg.Any<CancellationToken>()).Returns(false);

        await Should.ThrowAsync<KeyNotFoundException>(() => _handler.Handle(new PreviewTakenWorksQuery(_request), CancellationToken.None));

        await _removal.DidNotReceiveWithAnyArgs().FindAsync(default!, default, default);
    }
}
