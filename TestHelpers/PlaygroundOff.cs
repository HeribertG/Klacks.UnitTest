// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The real Playground collaborators built on a mode that is off (IsActive false), for tests that construct a class
/// which requires them but exercise the customer-installation behaviour. They are the null objects of the Playground
/// feature: the conversation store reports itself inactive and the model pin hands every requested model back untouched.
/// </summary>

using Klacks.Api.Application.Services.Playground;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Services.Playground;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.TestHelpers;

public static class PlaygroundOff
{
    public static IPlaygroundMode Mode { get; } = new InactivePlaygroundMode();

    public static IPlaygroundConversationStore ConversationStore() =>
        new PlaygroundConversationStore(Mode, TimeProvider.System);

    public static IPlaygroundModelPin ModelPin() =>
        new PlaygroundModelPin(Mode, Options.Create(new PlaygroundModelOptions()));
}
