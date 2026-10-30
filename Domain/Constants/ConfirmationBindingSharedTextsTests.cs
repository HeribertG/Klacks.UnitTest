// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The refusal texts and the binding kinds a plugin skill shares with the host: the model-facing BindingRequired and
/// ScopeChanged texts have one source (the plugin contract), which the host's ConfirmationBindingMessages re-exports, so a
/// plugin skill and a host skill refuse in the same words; and the kind of the message binding is distinct from every other kind
/// (a binding made for one skill can never pin another).
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Plugin.Contracts.Skills;
using Klacks.Plugin.Messaging.Application.Constants;

namespace Klacks.UnitTest.Domain.Constants;

[TestFixture]
public class ConfirmationBindingSharedTextsTests
{
    [Test]
    public void TheHostTexts_AreTheContractTexts()
    {
        ConfirmationBindingMessages.BindingRequired.ShouldBe(PluginConfirmationTexts.BindingRequired);
        ConfirmationBindingMessages.ScopeChanged.ShouldBe(PluginConfirmationTexts.ScopeChanged);
    }

    [Test]
    public void TheBroadcastKind_IsTheHostKind_AndNotTheMessageKind()
    {
        ConfirmationBindingKinds.Broadcast.ShouldBe(MessagingConstants.BroadcastBindingKind);
        ConfirmationBindingKinds.Broadcast.ShouldNotBe(ConfirmationBindingKinds.Message);
    }

    [Test]
    public void TheMessageKind_IsTheHostKind_AndDistinctFromTheOthers()
    {
        ConfirmationBindingKinds.Message.ShouldBe(MessagingConstants.MessageBindingKind);
        new[]
        {
            ConfirmationBindingKinds.Work,
            ConfirmationBindingKinds.MacroSwitch,
            ConfirmationBindingKinds.MacroUndo,
            ConfirmationBindingKinds.Message
        }.Distinct(StringComparer.Ordinal).Count().ShouldBe(4);
    }
}
