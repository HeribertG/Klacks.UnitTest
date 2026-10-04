// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The access scope is what a supervisor's message list and broadcast audience go through: only messages and
/// clients of visible groups, never a message without a client, full pages even when hidden messages are
/// interleaved, and the ascending order of the unrestricted list.
/// </summary>

using Klacks.Plugin.Contracts;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Application.Services;
using Klacks.Plugin.Messaging.Domain.Enums;
using Klacks.Plugin.Messaging.Domain.Models;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Plugins.Messaging;

[TestFixture]
public class MessagingAccessScopeTests
{
    private const int RawPageSize = MessagingConstants.MaxMessageQueryCount;

    private IMessagingService _messagingService = null!;
    private IClientVisibilityReader _clientVisibility = null!;
    private IClientGroupReader _clientGroupReader = null!;
    private IClientIdNumberReader _clientIdNumberReader = null!;
    private MessagingAccessScope _sut = null!;
    private Guid _hiddenClientId;
    private Guid _visibleClientId;

    [SetUp]
    public void Setup()
    {
        _messagingService = Substitute.For<IMessagingService>();
        _clientVisibility = Substitute.For<IClientVisibilityReader>();
        _clientGroupReader = Substitute.For<IClientGroupReader>();
        _clientIdNumberReader = Substitute.For<IClientIdNumberReader>();
        _hiddenClientId = Guid.NewGuid();
        _visibleClientId = Guid.NewGuid();
        _clientVisibility.IsClientVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<Guid>() != _hiddenClientId);

        _sut = new MessagingAccessScope(_messagingService, _clientVisibility, _clientGroupReader, _clientIdNumberReader);
    }

    [Test]
    public async Task GetVisibleMessages_InternalScope_EmptyWithoutReading()
    {
        var result = await _sut.GetVisibleMessagesAsync(null, null, null, MessageScope.Internal, 50, 0);

        result.ShouldBeEmpty();
        await _messagingService.DidNotReceiveWithAnyArgs().GetMessagesAsync(default, default, default, default, default, default, default);
    }

    [Test]
    public async Task GetVisibleMessages_ReadsClientScopeOnly_AndDropsHiddenAndClientlessMessages()
    {
        var visible = NewMessage(_visibleClientId);
        var page = new List<Message> { NewMessage(_hiddenClientId), NewMessage(null), visible };
        GivePages(page);

        var result = await _sut.GetVisibleMessagesAsync(null, null, null, null, 50, 0);

        result.ShouldBe(new[] { visible });
        await _messagingService.Received(1).GetMessagesAsync(
            null, null, null, MessageScope.Client, RawPageSize, 0, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetVisibleMessages_HiddenMessagesInterleaved_FillsThePageFromOlderRawPages_InAscendingOrder()
    {
        var olderVisible = NewMessage(_visibleClientId);
        var olderPage = new List<Message> { olderVisible };
        var newerPage = Enumerable.Range(0, RawPageSize - 1).Select(_ => NewMessage(_hiddenClientId)).ToList();
        var newestVisible = NewMessage(_visibleClientId);
        newerPage.Add(newestVisible);
        GivePages(newerPage, olderPage);

        var result = await _sut.GetVisibleMessagesAsync(null, null, null, null, 2, 0);

        result.ShouldBe(new[] { olderVisible, newestVisible });
    }

    [Test]
    public async Task GetVisibleMessages_OffsetCountsVisibleMessagesOnly()
    {
        var first = NewMessage(_visibleClientId);
        var second = NewMessage(_hiddenClientId);
        var third = NewMessage(_visibleClientId);
        GivePages(new List<Message> { first, second, third });

        var result = await _sut.GetVisibleMessagesAsync(null, null, null, null, 1, 1);

        result.ShouldBe(new[] { first });
    }

    [Test]
    public async Task GetVisibleMessages_AsksTheHostOncePerClient()
    {
        GivePages(new List<Message> { NewMessage(_visibleClientId), NewMessage(_visibleClientId), NewMessage(_hiddenClientId), NewMessage(_hiddenClientId) });

        await _sut.GetVisibleMessagesAsync(null, null, null, null, 50, 0);

        await _clientVisibility.Received(1).IsClientVisibleAsync(_visibleClientId, Arg.Any<CancellationToken>());
        await _clientVisibility.Received(1).IsClientVisibleAsync(_hiddenClientId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task GetVisibleMessages_StopsAfterTheScanLimit()
    {
        var fullHiddenPage = Enumerable.Range(0, RawPageSize).Select(_ => NewMessage(_hiddenClientId)).ToList();
        _messagingService.GetMessagesAsync(null, null, null, MessageScope.Client, RawPageSize, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(fullHiddenPage);

        var result = await _sut.GetVisibleMessagesAsync(null, null, null, null, 50, 0);

        result.ShouldBeEmpty();
        await _messagingService.Received(MessagingConstants.MaxVisibleMessagePageScans).GetMessagesAsync(
            null, null, null, MessageScope.Client, RawPageSize, Arg.Any<int>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task IsClientVisible_NoClient_False()
    {
        (await _sut.IsClientVisibleAsync(null)).ShouldBeFalse();
    }

    [Test]
    public async Task GetVisibleGroupClientIds_DropsHiddenMembers()
    {
        var groupId = Guid.NewGuid();
        _clientGroupReader.GetClientIdsInGroupAsync(groupId, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { _visibleClientId, _hiddenClientId, _visibleClientId });

        var result = await _sut.GetVisibleGroupClientIdsAsync(groupId);

        result.ShouldBe(new[] { _visibleClientId });
    }

    [Test]
    public async Task GetVisibleIdNumberClientIds_DropsHiddenClients()
    {
        var idNumbers = new[] { 1, 2 };
        _clientIdNumberReader.GetClientIdsByIdNumbersAsync(idNumbers, Arg.Any<CancellationToken>())
            .Returns(new List<Guid> { _hiddenClientId, _visibleClientId });

        var result = await _sut.GetVisibleIdNumberClientIdsAsync(idNumbers);

        result.ShouldBe(new[] { _visibleClientId });
    }

    private void GivePages(params List<Message>[] newestFirstPages)
    {
        for (var i = 0; i < newestFirstPages.Length; i++)
        {
            _messagingService.GetMessagesAsync(null, null, null, MessageScope.Client, RawPageSize, i * RawPageSize, Arg.Any<CancellationToken>())
                .Returns(newestFirstPages[i]);
        }
    }

    private static Message NewMessage(Guid? clientId)
    {
        return new Message { Id = Guid.NewGuid(), ClientId = clientId };
    }
}
