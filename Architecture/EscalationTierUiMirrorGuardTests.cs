// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Drift guard for the hand-written mirror of the engine's EscalationTier in Klacks.Ui
/// (src/app/domain/enums/escalation-tier.enum.ts). The integers leave the engine as API result fields and pick the
/// label key of the absence cover dialog, so a swapped or missing member shows the wrong text without any error.
/// The TypeScript file is scanned as source; when Klacks.Ui is not checked out next to Klacks.Api the guard reports
/// inconclusive, a checked-out Klacks.Ui without the file is a failure.
/// </summary>

using System.Globalization;
using System.Text.RegularExpressions;
using Klacks.ScheduleRecovery.Model;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class EscalationTierUiMirrorGuardTests
{
    private const string UiProjectDirectory = "Klacks.Ui";

    private static readonly string[] EnumFileSegments =
    [
        "src", "app", "domain", "enums", "escalation-tier.enum.ts"
    ];

    private static readonly Regex EnumBody = new(
        @"export\s+enum\s+EscalationTier\s*\{(?<body>[^}]*)\}",
        RegexOptions.Compiled);

    private static readonly Regex EnumMember = new(
        @"(?<name>[A-Za-z_][A-Za-z0-9_]*)\s*=\s*(?<value>-?\d+)",
        RegexOptions.Compiled);

    [Test]
    public void UiEscalationTier_MustMirrorTheEngineEnumByNameAndValue()
    {
        var uiProject = RepositoryRootLocator.FindDirectory(UiProjectDirectory);
        if (uiProject == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiProjectDirectory));
            return;
        }

        var path = Path.Combine(uiProject, Path.Combine(EnumFileSegments));
        File.Exists(path).ShouldBeTrue($"The Angular mirror of EscalationTier is missing: {path}");

        var uiMembers = ReadMembers(File.ReadAllText(path));
        var engineMembers = Enum.GetValues<EscalationTier>()
            .ToDictionary(tier => tier.ToString(), tier => (int)tier);

        uiMembers.OrderBy(member => member.Key).ShouldBe(engineMembers.OrderBy(member => member.Key));
    }

    [Test]
    public void TheScan_ReadsNamesAndValuesFromAnEnumSource()
    {
        var members = ReadMembers("export enum EscalationTier {\n  First = 0,\n  Second = 5,\n}\n");

        members.ShouldBe(new Dictionary<string, int> { ["First"] = 0, ["Second"] = 5 });
    }

    private static Dictionary<string, int> ReadMembers(string source)
    {
        var body = EnumBody.Match(source);
        body.Success.ShouldBeTrue("The file declares no exported enum EscalationTier.");

        return EnumMember.Matches(body.Groups["body"].Value)
            .ToDictionary(
                member => member.Groups["name"].Value,
                member => int.Parse(member.Groups["value"].Value, CultureInfo.InvariantCulture));
    }
}
