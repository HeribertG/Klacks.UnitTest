// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for MessengerReplySender: it serves messenger requests, resolves the personal contact of
/// the request's messenger channel through IClientMessengerReplyChannel (no contact, no target), never
/// uses the sender id of the incoming message as recipient, sends through the same channel, and keeps
/// its constructor down to the core-owned reply channel so it stays a DI leaf.
/// </summary>

using Klacks.Api.Domain.Interfaces.Inbound;
using Klacks.Api.Domain.Models.Inbound;
using Klacks.Api.Infrastructure.Inbound;

namespace Klacks.UnitTest.Infrastructure.Inbound;

[TestFixture]
public class MessengerReplySenderTests
{
    private static readonly Guid ClientId = Guid.NewGuid();

    private IClientMessengerReplyChannel _replyChannel = null!;
    private MessengerReplySender _sender = null!;

    [SetUp]
    public void SetUp()
    {
        _replyChannel = Substitute.For<IClientMessengerReplyChannel>();
        _sender = new MessengerReplySender(_replyChannel);
    }

    private static ClarificationRequest Request() => new(
        ClientId: ClientId,
        ClientType: EntityTypeEnum.Employee,
        Source: new InboundSource(
            SourceId: Guid.NewGuid(),
            SourceKind: InboundSourceKind.Messenger,
            Channel: "Messenger:Slack",
            SenderDisplay: "Anna Muster",
            Subject: null,
            Body: "Mir geht's nicht gut",
            ReceivedAt: DateTime.UtcNow),
        ReplyChannel: "Slack",
        SenderAddress: "C024BE91L",
        EmailThread: null);

    [Test]
    public void Constructor_TakesOnlyTheReplyChannel_SoItStaysADependencyLeaf()
    {
        var parameterTypes = typeof(MessengerReplySender).GetConstructors().Single()
            .GetParameters().Select(p => p.ParameterType).ToList();

        Assert.That(parameterTypes, Is.EquivalentTo(new[] { typeof(IClientMessengerReplyChannel) }));
    }

    [Test]
    public void SourceKind_IsMessenger()
    {
        _sender.SourceKind.ShouldBe(InboundSourceKind.Messenger);
    }

    [Test]
    public async Task ResolveTarget_UsesThePersonalContactOfTheSameMessenger()
    {
        _replyChannel.ResolvePersonalRecipientAsync(ClientId, "Slack", Arg.Any<CancellationToken>()).Returns("U0BLRED0TK2");

        var target = await _sender.ResolveTargetAsync(Request());

        target.ShouldNotBeNull();
        target.Recipient.ShouldBe("U0BLRED0TK2");
        target.Subject.ShouldBeNull();
    }

    [Test]
    public async Task ResolveTarget_NoPersonalContact_ReturnsNull()
    {
        _replyChannel.ResolvePersonalRecipientAsync(ClientId, "Slack", Arg.Any<CancellationToken>()).Returns((string?)null);

        (await _sender.ResolveTargetAsync(Request())).ShouldBeNull();
    }

    [Test]
    public async Task Send_DelegatesToTheReplyChannel()
    {
        _replyChannel.SendAsync("Slack", "U0BLRED0TK2", "Frage?", Arg.Any<CancellationToken>()).Returns(InboundReplyResult.Sent);

        var result = await _sender.SendAsync(Request(), new InboundReplyTarget("U0BLRED0TK2", null, null, null), "Frage?");

        result.Success.ShouldBeTrue();
        await _replyChannel.Received(1).SendAsync("Slack", "U0BLRED0TK2", "Frage?", Arg.Any<CancellationToken>());
        await _replyChannel.DidNotReceive().SendAsync(Arg.Any<string>(), "C024BE91L", Arg.Any<string>(), Arg.Any<CancellationToken>());
    }
}
