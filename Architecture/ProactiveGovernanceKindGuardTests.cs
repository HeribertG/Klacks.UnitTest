// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Architecture guard keeping ProactiveGovernanceDefaults.GovernedKinds in step with the trigger event
/// classes it is derived from. A governance rule steers what happens to a CONDITION, and only a
/// ledger-tracked event ever becomes one - AgentConditionLedgerPolicy.IsLedgerTracked spells that out
/// as "no TargetUserId, and PlannersOnly or AdminOnly". Adding a trigger event that satisfies that and
/// forgetting the list would leave the new kind without a row in the settings card, and listing a kind
/// that never reaches the ledger would show administrators a control that governs nothing.
///
/// A source scan is used deliberately. The three audience members are interface default
/// implementations that the event records override with expression bodies computed from constructor
/// arguments (TargetUserId =&gt; UserId), so reflection cannot read them without constructing each record,
/// and an uninitialised instance would report Guid.Empty rather than null - misclassifying exactly the
/// per-user events this guard has to exclude.
///
/// Scope note: the scan matches the overrides verbatim as they are written today
/// ("PlannersOnly =&gt; true"). An event that computed its audience at runtime, or spelled the override
/// with a block body, would be read as using the interface default (false) and would silently drop out
/// of the governed set.
/// </summary>

using Klacks.Api.Domain.Constants;
using System.Text.RegularExpressions;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ProactiveGovernanceKindGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string TriggerEventDirectory = "Application";
    private const string TriggerEventFilePattern = "*TriggerEvent.cs";
    private const int MinimumScannedFiles = 15;

    private static readonly Regex KindDeclaration =
        new(@"Kind\s*=>\s*AgentTriggerKinds\.(?<member>[A-Za-z0-9_]+)", RegexOptions.Compiled);

    private static readonly Regex TargetUserIdOverride =
        new(@"TargetUserId\s*=>", RegexOptions.Compiled);

    private static readonly Regex PlannersOnlyOverride =
        new(@"PlannersOnly\s*=>\s*true", RegexOptions.Compiled);

    private static readonly Regex AdminOnlyOverride =
        new(@"AdminOnly\s*=>\s*true", RegexOptions.Compiled);

    [Test]
    public void GovernedKinds_MatchTheLedgerTrackedTriggerEvents()
    {
        // Arrange
        var ledgerTracked = ScanLedgerTrackedKinds();

        // Act
        var governed = ProactiveGovernanceDefaults.GovernedKinds.ToHashSet(StringComparer.Ordinal);

        // Assert
        var missing = ledgerTracked.Except(governed).OrderBy(kind => kind, StringComparer.Ordinal).ToList();
        var surplus = governed.Except(ledgerTracked).OrderBy(kind => kind, StringComparer.Ordinal).ToList();

        Assert.Multiple(() =>
        {
            Assert.That(
                missing,
                Is.Empty,
                "These trigger kinds reach the condition ledger but carry no governance rule, so nobody " +
                "can steer how far Klacksy may act on them. Add them to " +
                $"{nameof(ProactiveGovernanceDefaults)}.{nameof(ProactiveGovernanceDefaults.GovernedKinds)} " +
                $"and seed a default row: {string.Join(", ", missing)}");

            Assert.That(
                surplus,
                Is.Empty,
                "These trigger kinds never become a condition, so a governance rule for them governs " +
                "nothing and only misleads the administrator reading the settings card: " +
                $"{string.Join(", ", surplus)}");
        });
    }

    [Test]
    public void GovernedKinds_AreAllKnownTriggerKinds()
    {
        // Arrange
        var known = typeof(AgentTriggerKinds)
            .GetFields()
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .Select(field => (string)field.GetRawConstantValue()!)
            .ToHashSet(StringComparer.Ordinal);

        // Act
        var unknown = ProactiveGovernanceDefaults.GovernedKinds
            .Where(kind => !known.Contains(kind))
            .ToList();

        // Assert
        Assert.That(
            unknown,
            Is.Empty,
            $"These governed kinds are not declared in {nameof(AgentTriggerKinds)}: " +
            string.Join(", ", unknown));
    }

    private static HashSet<string> ScanLedgerTrackedKinds()
    {
        var kindsByMemberName = typeof(AgentTriggerKinds)
            .GetFields()
            .Where(field => field.IsLiteral && field.FieldType == typeof(string))
            .ToDictionary(field => field.Name, field => (string)field.GetRawConstantValue()!, StringComparer.Ordinal);

        var directory = Path.Combine(LocateApiProject(), TriggerEventDirectory);
        var files = Directory
            .EnumerateFiles(directory, TriggerEventFilePattern, SearchOption.AllDirectories)
            .ToList();

        Assert.That(
            files, Has.Count.GreaterThanOrEqualTo(MinimumScannedFiles),
            $"Only {files.Count} trigger event files were scanned under {directory}; the guard would " +
            "pass vacuously.");

        var tracked = new HashSet<string>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var source = File.ReadAllText(file);
            var kindMatch = KindDeclaration.Match(source);
            if (!kindMatch.Success
                || !kindsByMemberName.TryGetValue(kindMatch.Groups["member"].Value, out var kind))
            {
                continue;
            }

            var hasAudienceGate = PlannersOnlyOverride.IsMatch(source) || AdminOnlyOverride.IsMatch(source);
            if (hasAudienceGate && !TargetUserIdOverride.IsMatch(source))
            {
                tracked.Add(kind);
            }
        }

        return tracked;
    }

    private static string LocateApiProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (Directory.Exists(Path.Combine(candidate, TriggerEventDirectory)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the {ApiProjectDirectory} project by walking up from the test base directory.");
    }
}
