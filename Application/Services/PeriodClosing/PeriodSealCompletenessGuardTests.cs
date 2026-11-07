// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for PeriodSealCompletenessGuard: a complete period costs one count and no list, an incomplete one is
/// refused with the full count but a list restricted to the clients the caller may see, a sealed period with services on
/// stand-ins is refused with StandInWorksPresentException, and the scope (group or whole period) travels to the
/// repository unchanged.
/// </summary>

using Klacks.Api.Application.DTOs.PeriodClosing;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Services.PeriodClosing;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Application.Services.PeriodClosing;

[TestFixture]
public class PeriodSealCompletenessGuardTests
{
    private static readonly DateOnly Start = new(2026, 11, 1);
    private static readonly DateOnly End = new(2026, 11, 30);
    private static readonly Guid GroupId = Guid.Parse("7a1d0000-0000-0000-0000-00000000000a");
    private static readonly Guid VisibleClientId = Guid.Parse("11111111-0000-0000-0000-000000000001");
    private static readonly Guid HiddenClientId = Guid.Parse("11111111-0000-0000-0000-000000000002");

    private IWorkRepository _workRepository = null!;

    private PeriodSealCompletenessGuard Create(params Guid[] hiddenClients)
    {
        _workRepository = Substitute.For<IWorkRepository>();
        return new PeriodSealCompletenessGuard(
            _workRepository,
            TestScheduleRowAccess.Over(new HiddenClientsVisibilityGuard(hiddenClients)));
    }

    private static UnsealedWorkDto Row(Guid clientId, string name, int day) =>
        new(Guid.NewGuid(), clientId, name, new DateOnly(2026, 11, day), new TimeOnly(8, 0), new TimeOnly(16, 0), "Early");

    [Test]
    public async Task EnsureAllSealed_DoesNothing_AndLoadsNoList_WhenEveryServiceIsSealed()
    {
        var guard = Create();
        _workRepository.CountUnsealedForCloseAsync(Start, End, GroupId, Arg.Any<CancellationToken>()).Returns(0);

        await guard.EnsureAllSealedAsync(Start, End, GroupId);

        await _workRepository.DidNotReceiveWithAnyArgs().ListUnsealedForCloseAsync(default, default, default, default, default);
    }

    [Test]
    public async Task EnsureAllSealed_RefusesWithTheFullCount_AndOnlyTheVisibleRows()
    {
        var guard = Create(HiddenClientId);
        var visibleRow = Row(VisibleClientId, "Elsa Muster", 3);
        _workRepository.CountUnsealedForCloseAsync(Start, End, null, Arg.Any<CancellationToken>()).Returns(2);
        _workRepository.ListUnsealedForCloseAsync(Start, End, null, PeriodSealCompletenessGuard.MaxListedWorks, Arg.Any<CancellationToken>())
            .Returns(new List<UnsealedWorkDto> { visibleRow, Row(HiddenClientId, "Secret Person", 4) });

        var refusal = await Should.ThrowAsync<UnsealedWorksException>(() => guard.EnsureAllSealedAsync(Start, End, null));

        refusal.UnsealedCount.ShouldBe(2);
        refusal.Listed.ShouldBe(new List<UnsealedWorkDto> { visibleRow });
    }

    [Test]
    public async Task EnsureAllSealed_NeverPutsAClientNameIntoTheLoggedMessage()
    {
        var guard = Create();
        _workRepository.CountUnsealedForCloseAsync(Start, End, GroupId, Arg.Any<CancellationToken>()).Returns(1);
        _workRepository.ListUnsealedForCloseAsync(Start, End, GroupId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<UnsealedWorkDto> { Row(VisibleClientId, "Elsa Muster", 3) });

        var refusal = await Should.ThrowAsync<UnsealedWorksException>(() => guard.EnsureAllSealedAsync(Start, End, GroupId));

        refusal.Message.ShouldNotContain("Elsa");
        refusal.Message.ShouldContain("1 service(s)");
    }

    [Test]
    public async Task EnsureAllSealed_ListsAtMostTheCap_AndReadsTheCountBeforeTheList()
    {
        var guard = Create();
        _workRepository.CountUnsealedForCloseAsync(Start, End, GroupId, Arg.Any<CancellationToken>()).Returns(500);
        _workRepository.ListUnsealedForCloseAsync(Start, End, GroupId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<UnsealedWorkDto> { Row(VisibleClientId, "Elsa Muster", 3) });

        var refusal = await Should.ThrowAsync<UnsealedWorksException>(() => guard.EnsureAllSealedAsync(Start, End, GroupId));

        refusal.UnsealedCount.ShouldBe(500);
        Received.InOrder(() =>
        {
            _workRepository.CountUnsealedForCloseAsync(Start, End, GroupId, Arg.Any<CancellationToken>());
            _workRepository.ListUnsealedForCloseAsync(Start, End, GroupId, PeriodSealCompletenessGuard.MaxListedWorks, Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task CountUnsealed_PassesTheScopeToTheRepository()
    {
        var guard = Create();
        _workRepository.CountUnsealedForCloseAsync(Start, End, GroupId, Arg.Any<CancellationToken>()).Returns(4);

        (await guard.CountUnsealedAsync(Start, End, GroupId)).ShouldBe(4);
    }

    [TestCase(true)]
    [TestCase(false)]
    public async Task EnsureAllSealed_StandInWork_Blocks_EvenWhenEveryServiceIsSealed(bool groupClose)
    {
        var guard = Create();
        Guid? groupId = groupClose ? GroupId : null;
        _workRepository.CountUnsealedForCloseAsync(Start, End, groupId, Arg.Any<CancellationToken>()).Returns(0);
        _workRepository.CountStandInWorksForCloseAsync(Start, End, groupId, Arg.Any<CancellationToken>()).Returns(2);

        var refusal = await Should.ThrowAsync<StandInWorksPresentException>(() => guard.EnsureAllSealedAsync(Start, End, groupId));

        refusal.StandInWorkCount.ShouldBe(2);
        refusal.ConflictCode.ShouldBe(StandInWorksPresentException.ErrorCode);
        refusal.ConflictDetails![StandInWorksPresentException.StandInWorkCountKey].ShouldBe(2);
    }

    [Test]
    public async Task EnsureAllSealed_WithoutStandInWorks_Passes_AfterCountingThem()
    {
        var guard = Create();
        _workRepository.CountUnsealedForCloseAsync(Start, End, GroupId, Arg.Any<CancellationToken>()).Returns(0);
        _workRepository.CountStandInWorksForCloseAsync(Start, End, GroupId, Arg.Any<CancellationToken>()).Returns(0);

        await guard.EnsureAllSealedAsync(Start, End, GroupId);

        await _workRepository.Received(1).CountStandInWorksForCloseAsync(Start, End, GroupId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CountStandInWorks_PassesTheScopeToTheRepository()
    {
        var guard = Create();
        _workRepository.CountStandInWorksForCloseAsync(Start, End, GroupId, Arg.Any<CancellationToken>()).Returns(5);

        (await guard.CountStandInWorksAsync(Start, End, GroupId)).ShouldBe(5);
    }
}
