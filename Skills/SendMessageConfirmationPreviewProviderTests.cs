// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The server-computed preview of send_message: it resolves the recipient with the very resolver the skill uses, shows the
/// channel, the resolved recipient (display name and address) and the text verbatim, and binds provider, address, content
/// type and a hash of the text as a binding of the message kind. A call the skill would refuse (unknown or hidden contact,
/// ambiguous name, an owner or user target for a non-admin, a missing parameter) is refused here, so the gate issues no token;
/// the caller's admin right is what the resolver is asked with.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.Plugin.Messaging.Application.Interfaces;
using Klacks.Plugin.Messaging.Application.Models;
using Klacks.Plugin.Messaging.Application.Services;
using Klacks.Plugin.Messaging.Domain.Models;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SendMessageConfirmationPreviewProviderTests
{
    private IMessageRecipientResolver _resolver = null!;
    private SendMessageConfirmationPreviewProvider _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _resolver = Substitute.For<IMessageRecipientResolver>();
        _sut = new SendMessageConfirmationPreviewProvider(_resolver);
    }

    private static SkillExecutionContext Context(params string[] permissions) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "caller",
        UserPermissions = permissions.ToList()
    };

    private static Dictionary<string, object> Parameters(string content = "Hallo, die Schicht morgen faellt aus.") => new()
    {
        ["provider"] = "telegram",
        ["recipient"] = "Hans Muster",
        ["content"] = content
    };

    private void ResolvesTo(string provider = "telegram", string display = "Muster, Hans", string identifier = "chat-123") =>
        _resolver.ResolveAsync(Arg.Any<SendMessageParameters>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(MessageRecipientResolution.Resolved(provider, display, identifier));

    [Test]
    public void Supports_OnlyTheSendMessageSkill()
    {
        _sut.Supports("send_message").ShouldBeTrue();
        _sut.Supports("SEND_MESSAGE").ShouldBeTrue();
        _sut.Supports("read_messages").ShouldBeFalse();
        _sut.Supports("bulk_delete_works").ShouldBeFalse();
    }

    [Test]
    public async Task ThePreview_ShowsChannelRecipientAndTheTextVerbatim()
    {
        ResolvesTo();
        const string text = "Hallo, die Schicht morgen faellt aus.\n\nBitte \"Ja\" antworten.";

        var preview = await _sut.BuildAsync("send_message", Context(Roles.Admin), Parameters(text));

        preview.IsRefusal.ShouldBeFalse();
        preview.Text.ShouldContain("telegram");
        preview.Text.ShouldContain("Muster, Hans");
        preview.Text.ShouldContain("chat-123");
        preview.Text.ShouldContain(text);
    }

    [Test]
    public async Task TheBinding_IsOfTheMessageKind_AndCarriesTheSameItemsTheSkillCompares()
    {
        ResolvesTo();
        var parameters = Parameters();

        var preview = await _sut.BuildAsync("send_message", Context(Roles.Admin), parameters);

        preview.Binding.ShouldNotBeNull();
        preview.Binding!.Kind.ShouldBe(ConfirmationBindingKinds.Message);
        preview.Binding.Items.ShouldNotBeEmpty("the items must be stored whole, the plugin compares them");
        preview.Binding.Items.OrderBy(item => item, StringComparer.Ordinal).ShouldBe(
            SendMessageBinding.ItemsFor(
                    MessageRecipientResolution.Resolved("telegram", "Muster, Hans", "chat-123"),
                    (string)parameters["content"],
                    "text")
                .OrderBy(item => item, StringComparer.Ordinal));
    }

    [Test]
    public async Task TheBinding_DiffersWhenTheTextDiffers()
    {
        ResolvesTo();

        var first = await _sut.BuildAsync("send_message", Context(Roles.Admin), Parameters("Text A"));
        var second = await _sut.BuildAsync("send_message", Context(Roles.Admin), Parameters("Text B"));

        first.Binding!.Matches(second.Binding!).ShouldBeFalse();
    }

    [TestCase(true, new[] { Roles.Admin })]
    [TestCase(false, new[] { Roles.Authorised, Permissions.CanEditClients })]
    public async Task TheResolver_IsAskedWithTheCallersAdminRight(bool expectedAdmin, string[] permissions)
    {
        ResolvesTo();

        await _sut.BuildAsync("send_message", Context(permissions), Parameters());

        await _resolver.Received(1).ResolveAsync(
            Arg.Is<SendMessageParameters>(p => p.Recipient == "Hans Muster" && p.Provider == "telegram"),
            expectedAdmin,
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task AnUnresolvableRecipient_IsRefusedWithTheResolversError_AndBindsNothing()
    {
        const string error = "No Telegram contact found for 'Hans Muster'. The client must have a Telegram entry in their Messenger tab.";
        _resolver.ResolveAsync(Arg.Any<SendMessageParameters>(), Arg.Any<bool>(), Arg.Any<CancellationToken>())
            .Returns(MessageRecipientResolution.Failed(error));

        var preview = await _sut.BuildAsync("send_message", Context(Permissions.CanEditClients), Parameters());

        preview.IsRefusal.ShouldBeTrue();
        preview.Text.ShouldBe(error);
        preview.Binding.ShouldBeNull();
    }

    [TestCase("recipient")]
    [TestCase("content")]
    public async Task AMissingRequiredParameter_IsRefused_WithoutAskingTheResolver(string missing)
    {
        var parameters = Parameters();
        parameters.Remove(missing);

        var preview = await _sut.BuildAsync("send_message", Context(Roles.Admin), parameters);

        preview.IsRefusal.ShouldBeTrue();
        preview.Text.ShouldContain(missing);
        preview.Binding.ShouldBeNull();
        await _resolver.DidNotReceiveWithAnyArgs().ResolveAsync(default!, default, default);
    }
}
