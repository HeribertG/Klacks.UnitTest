// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Test doubles for the group-visibility rule of received emails. A bare IEmailQueryRepository substitute
/// returns null lists, and a bare guard hides everything, so the email handler tests that are not about
/// visibility use these stand-ins: no sender belongs to a client, and every client is visible.
/// </summary>

namespace Klacks.UnitTest.Handlers.Email;

internal static class EmailVisibilityTestDoubles
{
    public static IEmailQueryRepository SendersWithoutClients()
    {
        var repository = Substitute.For<IEmailQueryRepository>();
        repository.GetClientIdsByEmailAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(_ => new List<Guid>());
        repository.GetClientsWithEmailCommunicationsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => new List<ClientEmailInfo>());
        return repository;
    }

    /// <summary>
    /// Repository in which the given sender address belongs to exactly the given clients.
    /// </summary>
    /// <param name="senderAddress">Sender address of the email under test</param>
    /// <param name="ownerIds">Clients whose private or office address equals the sender address</param>
    public static IEmailQueryRepository SenderOwnedBy(string senderAddress, params Guid[] ownerIds)
    {
        var repository = Substitute.For<IEmailQueryRepository>();
        repository.GetClientIdsByEmailAddressAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(ci => string.Equals(ci.Arg<string>(), senderAddress, StringComparison.OrdinalIgnoreCase)
                ? ownerIds.ToList()
                : new List<Guid>());
        repository.GetClientsWithEmailCommunicationsAsync(Arg.Any<CancellationToken>())
            .Returns(_ => ownerIds
                .Select(id => new ClientEmailInfo { ClientId = id, EmailAddress = senderAddress })
                .ToList());
        return repository;
    }

    public static IClientVisibilityGuard AllClientsVisible() => ClientsHidden();

    /// <summary>
    /// Guard that hides exactly the given clients and passes every other client through.
    /// </summary>
    /// <param name="hiddenClientIds">Clients the caller may not see</param>
    public static IClientVisibilityGuard ClientsHidden(params Guid[] hiddenClientIds)
    {
        var hidden = hiddenClientIds.ToHashSet();
        var guard = Substitute.For<IClientVisibilityGuard>();
        guard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(ci => !hidden.Contains(ci.Arg<Guid>()));
        guard.AreAllVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyCollection<Guid>>().All(id => !hidden.Contains(id)));
        guard.FilterVisibleAsync(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<Func<Guid, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyCollection<Guid>>().Where(id => !hidden.Contains(id)).ToList());
        guard.FilterVisibleAsync(
                Arg.Any<IReadOnlyCollection<ClientEmailInfo>>(), Arg.Any<Func<ClientEmailInfo, Guid>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ci.Arg<IReadOnlyCollection<ClientEmailInfo>>().Where(c => !hidden.Contains(c.ClientId)).ToList());
        return guard;
    }
}
