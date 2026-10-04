// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Keeps AgentTriggerClientAggregateKinds.Values in step with the IClientAggregateTriggerEvent types it
/// stands for. The read side (inbox, reminder sweep) only knows a dispatch row's kind string and decides by
/// this list whether the workforce-wide ledger payload may be merged over a recipient's narrowed params. A
/// new aggregate event missing from the list would put every employee's name back into a supervisor's inbox;
/// a stale entry would only freeze a kind's counts, so both directions are asserted.
///
/// Kind is read off an uninitialised instance, as in AgentTriggerGroupScopedKindsGuardTests: every event
/// type is a sealed record whose Kind is an expression-bodied constant.
/// </summary>

using System.Runtime.CompilerServices;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class AgentTriggerClientAggregateKindsGuardTests
{
    private static IReadOnlyList<Type> AggregateEventTypes() =>
        typeof(IClientAggregateTriggerEvent).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }
                && typeof(IClientAggregateTriggerEvent).IsAssignableFrom(type))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();

    private static string KindOf(Type eventType) =>
        ((IAgentTriggerEvent)RuntimeHelpers.GetUninitializedObject(eventType)).Kind;

    [Test]
    public void CuratedListMatchesEveryClientAggregateEvent()
    {
        var declared = AggregateEventTypes().Select(KindOf).ToHashSet(StringComparer.Ordinal);
        var curated = AgentTriggerClientAggregateKinds.Values.ToHashSet(StringComparer.Ordinal);

        var missing = declared.Except(curated).OrderBy(kind => kind, StringComparer.Ordinal).ToList();
        var stale = curated.Except(declared).OrderBy(kind => kind, StringComparer.Ordinal).ToList();

        missing.ShouldBeEmpty(
            "An IClientAggregateTriggerEvent kind is absent from AgentTriggerClientAggregateKinds.Values, so the "
            + "inbox and the reminder sweep merge its workforce-wide ledger payload over a supervisor's narrowed "
            + "params. Missing: " + string.Join(", ", missing));

        stale.ShouldBeEmpty(
            "AgentTriggerClientAggregateKinds.Values names a kind no IClientAggregateTriggerEvent emits. "
            + "Stale: " + string.Join(", ", stale));
    }

    [Test]
    public void ReflectionFindsTheKnownAggregates()
    {
        AggregateEventTypes().Count.ShouldBeGreaterThanOrEqualTo(
            3,
            "Reflection found fewer client aggregate events than exist today, so the set comparison above "
            + "could pass vacuously.");
    }
}
