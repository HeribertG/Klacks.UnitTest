// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Until 2026-10-01 the ClientShiftPreferences endpoints (list by client, available shifts, bulk save)
/// ignored group visibility, so a group-restricted user could read and overwrite the shift preferences of
/// any employee whose id they knew. A hidden client must now be answered exactly like a missing one: an
/// empty list on reads, a not-found refusal on writes with nothing deleted or written.
/// </summary>

using Klacks.Api.Application.Commands.ClientShiftPreferences;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Handlers.ClientShiftPreferences;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.ClientShiftPreferences;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.ClientShiftPreferences;

[TestFixture]
public class ClientShiftPreferenceVisibilityTests
{
    private IClientShiftPreferenceRepository _repository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IUnitOfWork _unitOfWork = null!;
    private Guid _hiddenClientId;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IClientShiftPreferenceRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _hiddenClientId = Guid.NewGuid();

        _clientVisibilityGuard.IsVisibleAsync(_hiddenClientId, Arg.Any<CancellationToken>()).Returns(false);
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<List<ClientShiftPreferenceResource>>>>())
            .Returns(ci => ci.ArgAt<Func<Task<List<ClientShiftPreferenceResource>>>>(0)());
    }

    [Test]
    public async Task ListByClient_HiddenClient_IsAnsweredWithAnEmptyList_LikeAMissingClient()
    {
        _repository.GetByClientIdAsync(_hiddenClientId, Arg.Any<CancellationToken>())
            .Returns(new List<ClientShiftPreference> { new() { Id = Guid.NewGuid(), ClientId = _hiddenClientId } });
        var handler = new ListByClientQueryHandler(
            _repository, _clientVisibilityGuard, new ClientShiftPreferenceMapper(),
            Substitute.For<ILogger<ListByClientQueryHandler>>());

        var result = await handler.Handle(new ListByClientQuery(_hiddenClientId), CancellationToken.None);

        result.ShouldBeEmpty();
        await _repository.DidNotReceive().GetByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AvailableShifts_HiddenClient_IsAnsweredWithAnEmptyList_LikeAMissingClient()
    {
        _groupItemRepository.GetGroupTreeIdsForClientAsync(_hiddenClientId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { Guid.NewGuid() });
        var handler = new GetAvailableShiftsQueryHandler(
            _groupItemRepository, _clientVisibilityGuard, Substitute.For<ILogger<GetAvailableShiftsQueryHandler>>());

        var result = await handler.Handle(new GetAvailableShiftsQuery(_hiddenClientId), CancellationToken.None);

        result.ShouldBeEmpty();
        await _groupItemRepository.DidNotReceive()
            .GetGroupTreeIdsForClientAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Save_HiddenClient_IsRefusedLikeAMissingClient_NothingDeletedOrWritten()
    {
        var handler = new SaveClientShiftPreferencesCommandHandler(
            _repository, _clientVisibilityGuard, new ClientShiftPreferenceMapper(), _unitOfWork,
            Substitute.For<ILogger<SaveClientShiftPreferencesCommandHandler>>());
        var command = new SaveClientShiftPreferencesCommand(
            _hiddenClientId,
            [new ClientShiftPreferenceResource { ShiftId = Guid.NewGuid(), PreferenceType = ShiftPreferenceType.Blacklist }]);

        var ex = await Should.ThrowAsync<KeyNotFoundException>(
            () => handler.Handle(command, CancellationToken.None));

        ex.Message.ShouldBe($"Client with ID {_hiddenClientId} not found");
        await _repository.DidNotReceive().DeleteAllByClientIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
        await _repository.DidNotReceive().Add(Arg.Any<ClientShiftPreference>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}
