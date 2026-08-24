// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Keeps AgentTriggerGroupScopedKinds.Values in step with the IAgentTriggerEvent implementations it
/// stands for. The constant exists because AgentConditionRepository's planner-facing reads need the set as
/// a plain string array EF can translate, and because it belongs in Domain, which must not reference the
/// Application layer the event types live in - so the list is hand-written and this test is what stops it
/// drifting. Set equality is asserted in BOTH directions: a new RequiresGroupScope event missing from the
/// list would silently keep leaking its ungrouped rows to every scoped planner, and a stale entry would
/// silently withhold a kind that is no longer group-borne.
///
/// Reflection reads the two properties off uninitialised instances: every event type is a sealed record
/// with a positional constructor, and both Kind and RequiresGroupScope are expression-bodied constants
/// that touch no field. A future event whose Kind depends on its state fails loudly here rather than
/// producing a wrong answer.
/// </summary>

using System.Runtime.CompilerServices;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class AgentTriggerGroupScopedKindsGuardTests
{
    private static IReadOnlyList<Type> EventTypes() =>
        typeof(IAgentTriggerEvent).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false, ContainsGenericParameters: false }
                && typeof(IAgentTriggerEvent).IsAssignableFrom(type))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();

    private static (string Kind, bool RequiresGroupScope) Declaration(Type eventType)
    {
        try
        {
            var instance = (IAgentTriggerEvent)RuntimeHelpers.GetUninitializedObject(eventType);
            return (instance.Kind, instance.RequiresGroupScope);
        }
        catch (Exception exception)
        {
            throw new InvalidOperationException(
                $"{eventType.Name}.Kind or .RequiresGroupScope could not be read off an uninitialised "
                + "instance, which means one of them now depends on constructor state. Both must stay "
                + "constant per type - AgentTriggerGroupScopedKinds maps kind to scope requirement and "
                + "cannot express a per-instance answer.",
                exception);
        }
    }

    [Test]
    public void CuratedListMatchesEveryEventDeclaringRequiresGroupScope()
    {
        var declared = EventTypes()
            .Select(Declaration)
            .Where(declaration => declaration.RequiresGroupScope)
            .Select(declaration => declaration.Kind)
            .ToHashSet(StringComparer.Ordinal);

        var curated = AgentTriggerGroupScopedKinds.Values.ToHashSet(StringComparer.Ordinal);

        var missing = declared.Except(curated).OrderBy(kind => kind, StringComparer.Ordinal).ToList();
        var stale = curated.Except(declared).OrderBy(kind => kind, StringComparer.Ordinal).ToList();

        missing.ShouldBeEmpty(
            "An IAgentTriggerEvent declares RequiresGroupScope but its kind is absent from "
            + "AgentTriggerGroupScopedKinds.Values, so the ledger read path still hands that kind's "
            + "GroupId-null rows to every scoped planner while the live push already withholds them. "
            + "Missing: " + string.Join(", ", missing));

        stale.ShouldBeEmpty(
            "AgentTriggerGroupScopedKinds.Values names a kind no IAgentTriggerEvent declares "
            + "RequiresGroupScope for, so the ledger read path withholds rows nothing intends to scope. "
            + "Stale: " + string.Join(", ", stale));
    }

    [Test]
    public void ReflectionFindsEnoughEventsAndKindsForTheComparisonToMeanSomething()
    {
        var declarations = EventTypes().Select(Declaration).ToList();

        declarations.Count.ShouldBeGreaterThan(
            10,
            "Reflection found almost no IAgentTriggerEvent implementations, so the set comparison above "
            + "would pass vacuously.");

        declarations
            .Count(declaration => !declaration.RequiresGroupScope)
            .ShouldBeGreaterThan(
                0,
                "Every event declares RequiresGroupScope, which would make the ungated branch of "
                + "AgentConditionRepository's scope filter dead code - a sign the default flipped.");

        declarations
            .Select(declaration => declaration.Kind)
            .ShouldAllBe(kind => !string.IsNullOrWhiteSpace(kind));
    }
}
