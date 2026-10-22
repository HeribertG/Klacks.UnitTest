// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ClientVisibilityGuard is the shared check behind every read and write of client-owned data. These tests pin
/// its set logic; the visibility rule itself is covered against the real group filter in ClientSearchRepositoryTests.
/// </summary>

using Klacks.Api.Application.Services.Clients;
using Klacks.Api.Domain.Services.Common;

namespace Klacks.UnitTest.Services.Clients;

[TestFixture]
public class ClientVisibilityGuardTests
{
    private IClientSearchRepository _clientSearchRepository = null!;
    private ICallerGroupRestriction _callerGroupRestriction = null!;
    private ClientVisibilityGuard _guard = null!;
    private Guid _visibleId;
    private Guid _hiddenId;

    private sealed record Row(Guid ClientId, string Name);

    [SetUp]
    public void SetUp()
    {
        _visibleId = Guid.NewGuid();
        _hiddenId = Guid.NewGuid();
        _clientSearchRepository = Substitute.For<IClientSearchRepository>();
        _clientSearchRepository
            .FilterVisibleToCallerAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => (IReadOnlySet<Guid>)((IReadOnlyCollection<Guid>)call[0]).Where(id => id == _visibleId).ToHashSet());
        _callerGroupRestriction = Substitute.For<ICallerGroupRestriction>();
        _callerGroupRestriction.IsCallerUnrestrictedAsync().Returns(false);
        _guard = new ClientVisibilityGuard(_clientSearchRepository, _callerGroupRestriction);
    }

    // Admins and owner-less background work are answered without a query, so an unknown or soft-deleted client
    // reaches the handler's own existence check exactly as before the guard existed.
    [Test]
    public async Task UnrestrictedCaller_SeesEverythingWithoutAQuery()
    {
        _callerGroupRestriction.IsCallerUnrestrictedAsync().Returns(true);
        var rows = new List<Row> { new(_visibleId, "a"), new(_hiddenId, "b") };

        (await _guard.IsVisibleAsync(_hiddenId)).ShouldBeTrue();
        (await _guard.AreAllVisibleAsync([_visibleId, _hiddenId])).ShouldBeTrue();
        (await _guard.FilterVisibleAsync(rows, row => row.ClientId)).Count.ShouldBe(2);
        await _clientSearchRepository.DidNotReceive().IsVisibleToCallerAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _clientSearchRepository.DidNotReceive()
            .FilterVisibleToCallerAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AreAllVisible_OneHiddenId_IsFalse()
    {
        (await _guard.AreAllVisibleAsync([_visibleId, _hiddenId])).ShouldBeFalse();
    }

    [Test]
    public async Task AreAllVisible_OnlyVisibleIdsWithDuplicates_IsTrue()
    {
        (await _guard.AreAllVisibleAsync([_visibleId, _visibleId])).ShouldBeTrue();
    }

    [Test]
    public async Task AreAllVisible_NoIds_IsTrueWithoutQuery()
    {
        (await _guard.AreAllVisibleAsync([])).ShouldBeTrue();
        await _clientSearchRepository.DidNotReceive()
            .FilterVisibleToCallerAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task FilterVisible_DropsRowsOfHiddenClientsAndKeepsOrder()
    {
        var rows = new List<Row> { new(_visibleId, "a"), new(_hiddenId, "b"), new(_visibleId, "c") };

        var result = await _guard.FilterVisibleAsync(rows, row => row.ClientId);

        result.Select(row => row.Name).ShouldBe(new[] { "a", "c" });
        await _clientSearchRepository.Received(1)
            .FilterVisibleToCallerAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>());
    }
}
