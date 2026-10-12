// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Client visibility guard for tests that works for every row type: it hides exactly the given client ids and
/// passes every other id through. Without hidden ids it behaves like an unrestricted caller. Records whether a
/// filter was requested, so tests can prove an unrestricted path skipped it.
/// </summary>
/// <param name="hiddenClientIds">Clients the caller may not see</param>

namespace Klacks.UnitTest.TestHelpers;

internal sealed class HiddenClientsVisibilityGuard(params Guid[] hiddenClientIds) : IClientVisibilityGuard
{
    private readonly HashSet<Guid> _hidden = hiddenClientIds.ToHashSet();

    public int FilterCalls { get; private set; }

    public Task<bool> IsVisibleAsync(Guid clientId, CancellationToken cancellationToken = default)
        => Task.FromResult(!_hidden.Contains(clientId));

    public Task<bool> AreAllVisibleAsync(IReadOnlyCollection<Guid> clientIds, CancellationToken cancellationToken = default)
        => Task.FromResult(clientIds.All(id => !_hidden.Contains(id)));

    public Task<List<T>> FilterVisibleAsync<T>(
        IReadOnlyCollection<T> items, Func<T, Guid> clientIdOf, CancellationToken cancellationToken = default)
    {
        FilterCalls++;
        return Task.FromResult(items.Where(item => !_hidden.Contains(clientIdOf(item))).ToList());
    }
}
