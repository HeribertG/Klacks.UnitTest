// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

namespace Klacks.UnitTest.Skills;

/// <summary>
/// Client visibility guards for skill tests: one that sees every client and one that hides a single client.
/// A bare substitute would answer false and turn every lookup into "not found".
/// </summary>
internal static class SkillClientVisibility
{
    public static IClientVisibilityGuard AllVisible()
    {
        var guard = Substitute.For<IClientVisibilityGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
        guard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>()).Returns(true);
        guard.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Func<Guid, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<IReadOnlyCollection<Guid>>().ToList()));
        guard.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<Client>>(), Arg.Any<Func<Client, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<IReadOnlyCollection<Client>>().ToList()));
        return guard;
    }

    public static IClientVisibilityGuard Hiding(Guid hiddenClientId)
    {
        var guard = Substitute.For<IClientVisibilityGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(call => call.Arg<Guid>() != hiddenClientId);
        guard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => !call.Arg<IReadOnlyCollection<Guid>>().Contains(hiddenClientId));
        guard.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Func<Guid, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<IReadOnlyCollection<Guid>>().Where(id => id != hiddenClientId).ToList()));
        guard.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<Client>>(), Arg.Any<Func<Client, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(call => Task.FromResult(call.Arg<IReadOnlyCollection<Client>>().Where(c => c.Id != hiddenClientId).ToList()));
        return guard;
    }

    /// <summary>
    /// Replaces the given client id in a skill message with a fixed placeholder, so the answer for a hidden
    /// client can be compared with the answer for an unknown id even when the message echoes the id.
    /// </summary>
    /// <param name="message">Skill result message</param>
    /// <param name="clientId">Id that was passed to the skill</param>
    public static string? WithoutId(string? message, Guid clientId)
    {
        return message?.Replace(clientId.ToString(), "<client-id>", StringComparison.OrdinalIgnoreCase);
    }
}
