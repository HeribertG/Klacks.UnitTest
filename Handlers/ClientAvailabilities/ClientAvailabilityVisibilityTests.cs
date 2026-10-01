// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Until 2026-10-01 the availability list returned every client's hourly availability and the bulk upsert
/// wrote availability for any client id, regardless of group visibility. Entries of hidden clients must now be
/// left out of the list, and a bulk request touching a hidden client must be refused like an unknown client
/// without writing anything.
/// </summary>

using Klacks.Api.Application.Commands.ClientAvailabilities;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Handlers.ClientAvailabilities;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.ClientAvailabilities;
using Klacks.UnitTest.TestHelpers;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Handlers.ClientAvailabilities;

[TestFixture]
public class ClientAvailabilityVisibilityTests
{
    private static readonly DateOnly Day = new(2026, 3, 9);
    private const int Hour = 8;

    private IClientAvailabilityRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private Guid _visibleClientId;
    private Guid _hiddenClientId;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IClientAvailabilityRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _visibleClientId = Guid.NewGuid();
        _hiddenClientId = Guid.NewGuid();
    }

    [Test]
    public async Task List_LeavesOutEntriesOfHiddenClients()
    {
        var visible = NewEntity(_visibleClientId);
        var hidden = NewEntity(_hiddenClientId);
        _repository.GetByDateRange(Day, Day).Returns(new List<ClientAvailability> { visible, hidden });
        var guard = Substitute.For<IClientVisibilityGuard>();
        guard.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<ClientAvailability>>(), Arg.Any<Func<ClientAvailability, Guid>>(),
                Arg.Any<CancellationToken>())
            .Returns(ci => ci.ArgAt<IReadOnlyCollection<ClientAvailability>>(0)
                .Where(e => ci.ArgAt<Func<ClientAvailability, Guid>>(1)(e) != _hiddenClientId)
                .ToList());
        var handler = new ListQueryHandler(
            _repository, guard, new ClientAvailabilityMapper(), Substitute.For<ILogger<ListQueryHandler>>());

        var result = (await handler.Handle(new ListClientAvailabilitiesQuery(Day, Day), CancellationToken.None)).ToList();

        result.Select(r => r.ClientId).ShouldBe(new[] { _visibleClientId });
    }

    [Test]
    public async Task BulkUpdate_AnyHiddenClient_RefusedLikeUnknownClient_NothingWritten()
    {
        var request = new ClientAvailabilityBulkRequest
        {
            Items =
            [
                NewResource(_visibleClientId),
                NewResource(_hiddenClientId),
            ],
        };
        var handler = new BulkUpdateCommandHandler(
            _repository, TestGroupWriteVisibility.ClientsHidden(_hiddenClientId), _unitOfWork,
            new ClientAvailabilityMapper(), Substitute.For<ILogger<BulkUpdateCommandHandler>>());

        var ex = await Should.ThrowAsync<KeyNotFoundException>(() =>
            handler.Handle(new BulkUpdateClientAvailabilityCommand(request), CancellationToken.None));

        ex.Message.ShouldBe($"Client with ID {_hiddenClientId} not found");
        await _repository.DidNotReceive().BulkUpsert(Arg.Any<List<ClientAvailability>>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task BulkUpdate_AllClientsVisible_UpsertsEveryEntry()
    {
        var request = new ClientAvailabilityBulkRequest { Items = [NewResource(_visibleClientId)] };
        var handler = new BulkUpdateCommandHandler(
            _repository, TestGroupWriteVisibility.AllClientsVisible(), _unitOfWork,
            new ClientAvailabilityMapper(), Substitute.For<ILogger<BulkUpdateCommandHandler>>());

        var count = await handler.Handle(new BulkUpdateClientAvailabilityCommand(request), CancellationToken.None);

        count.ShouldBe(1);
        await _repository.Received(1).BulkUpsert(Arg.Is<List<ClientAvailability>>(l => l.Single().ClientId == _visibleClientId));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    private static ClientAvailability NewEntity(Guid clientId)
    {
        return new ClientAvailability { Id = Guid.NewGuid(), ClientId = clientId, Date = Day, Hour = Hour, IsAvailable = true };
    }

    private static ClientAvailabilityResource NewResource(Guid clientId)
    {
        return new ClientAvailabilityResource { ClientId = clientId, Date = Day, Hour = Hour, IsAvailable = true };
    }
}
