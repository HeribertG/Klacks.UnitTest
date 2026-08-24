// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Forces every IAgentTriggerDetector to be classified with respect to the condition ledger: it either
/// promises a complete fingerprint set (IAgentConditionFingerprintSource) or stands on one of the two
/// allowlists below, each of which carries the reason. A fourteenth detector fails this test on the day
/// it is added, which is the point - the alternative is that it silently joins the group whose ledger
/// rows are never resolved, and nobody notices.
///
/// Reflection rather than a source scan, deliberately, and unlike ForbidChallengeSchemeGuardTests:
/// there the violation is a call inside a method body, which reflection cannot see. Here the property
/// under test is whether a type implements an interface, which is exactly what reflection does see. A
/// source scan for cap idioms (".Take(", "MaxFindingsPerTick") was the first idea and is worse: after
/// the Etappe 3c refactor UnstaffedShift7dDetector's cap is spelled "FilterRowCount" and would match
/// no pattern, so the scan would wave through the very detector whose 7-day window is the subtlest cap
/// of the six.
///
/// Scope note - what this does NOT cover: that an implementation of GetActiveFingerprintsAsync really
/// is complete. Nothing static can prove that; DetectorFingerprintContainmentTests covers the half of
/// it that is checkable (every emitted event is contained in the scan).
/// </summary>

using Klacks.Api.Application.Services.Assistant.Triggers;
using Klacks.Api.Domain.Interfaces.Assistant;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class AgentTriggerDetectorLedgerClassificationTests
{
    /// <summary>
    /// Emits companion events (no audience gate, targeted at one user), which AgentConditionLedgerPolicy
    /// keeps out of the ledger entirely - no row, so nothing to resolve.
    /// </summary>
    private static readonly string[] CompanionDetectorsOutsideTheLedger =
    [
        nameof(CuriosityQuestionDetector)
    ];

    /// <summary>
    /// Ledger-tracked, but with no complete fingerprint source yet: their rows are opened and their
    /// LastSeenAtUtc is maintained, and they are never resolved automatically. The accepted, documented
    /// compromise of Etappe 3c - leaving a row open too long is recoverable, resolving a still-true
    /// condition on the strength of a capped scan is not. Consumers of open rows (3f, Etappe 5) must
    /// treat these kinds as "was true when last seen", not as "is true now".
    /// </summary>
    private static readonly string[] LedgerTrackedWithoutResolveReconciliation =
    [
        nameof(LockConflictDetector),
        nameof(ScenarioPendingDetector),
        nameof(PeriodCloseDueDetector),
        nameof(ContractExpiringSoonDetector),
        nameof(PeriodOverdueDetector)
    ];

    private static IReadOnlyList<Type> DetectorTypes() =>
        typeof(IAgentTriggerDetector).Assembly
            .GetTypes()
            .Where(type => type is { IsClass: true, IsAbstract: false }
                && typeof(IAgentTriggerDetector).IsAssignableFrom(type))
            .OrderBy(type => type.Name, StringComparer.Ordinal)
            .ToList();

    [Test]
    public void EveryDetectorEitherPromisesACompleteFingerprintSetOrIsExplicitlyExcused()
    {
        var excused = CompanionDetectorsOutsideTheLedger
            .Concat(LedgerTrackedWithoutResolveReconciliation)
            .ToHashSet(StringComparer.Ordinal);

        var unclassified = DetectorTypes()
            .Where(type => !typeof(IAgentConditionFingerprintSource).IsAssignableFrom(type))
            .Where(type => !excused.Contains(type.Name))
            .Select(type => type.Name)
            .ToList();

        unclassified.ShouldBeEmpty(
            "Every IAgentTriggerDetector must be classified against the condition ledger. Implement "
            + "IAgentConditionFingerprintSource so the tick can resolve the kind's rows, or add the "
            + "detector to one of the two allowlists in this file together with the reason. Never report "
            + "a capped DetectAsync result as a complete fingerprint set - everything beyond the cap "
            + "would be resolved every tick and re-armed on the next. Unclassified: "
            + string.Join(", ", unclassified));
    }

    [Test]
    public void TheAllowlistsNameOnlyDetectorsThatStillExist()
    {
        var detectorNames = DetectorTypes().Select(type => type.Name).ToHashSet(StringComparer.Ordinal);

        var stale = CompanionDetectorsOutsideTheLedger
            .Concat(LedgerTrackedWithoutResolveReconciliation)
            .Where(name => !detectorNames.Contains(name))
            .ToList();

        stale.ShouldBeEmpty(
            "An allowlist entry no longer matches any IAgentTriggerDetector. A renamed or deleted "
            + "detector leaves a dead excuse behind that would silently cover a future detector of the "
            + "same name. Stale: " + string.Join(", ", stale));
    }

    [Test]
    public void TheAllowlistsAreNotAllowedToSwallowEveryDetector()
    {
        var detectors = DetectorTypes();

        detectors.Count.ShouldBeGreaterThan(
            10,
            "Reflection found almost no detectors, so a green result above would be meaningless.");

        var fingerprintSources = detectors
            .Where(type => typeof(IAgentConditionFingerprintSource).IsAssignableFrom(type))
            .ToList();

        fingerprintSources.Count.ShouldBeGreaterThan(
            0,
            "Not a single detector implements IAgentConditionFingerprintSource, which means the tick "
            + "never reconciles resolutions and the first test above passes only because everything is "
            + "excused.");

        fingerprintSources
            .Select(type => type.Name)
            .ShouldNotContain(
                name => CompanionDetectorsOutsideTheLedger.Contains(name, StringComparer.Ordinal),
                "A companion detector's events never reach the ledger, so implementing "
                + "IAgentConditionFingerprintSource on one is dead code that suggests otherwise.");
    }
}
