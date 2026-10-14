// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Architecture guard for the planning-period opt-out of BulkAddWorksCommand. PlanningPeriodCheck.SkipForPlannerOutput
/// switches the membership/group-period check of the bulk write off; only callers that filter their placements one by one
/// before they write (wizard apply, propose_plan) or that recreate Works which already existed (harmonizer) may use it.
/// Any other caller would reopen the gap that let chat and REST plan outside an employee's membership. A source scan is
/// used because the violation is an argument at a call site, which reflection cannot see.
/// </summary>

using System.Text;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class PlanningPeriodSkipGuardTests
{
    private const string SkipLiteral = "PlanningPeriodCheck.SkipForPlannerOutput";
    private const string SourceFilePattern = "*.cs";
    private const string MigrationsDirectory = "Migrations";
    private const int MinimumScannedFiles = 100;

    private static readonly HashSet<string> AllowedFiles = new(StringComparer.OrdinalIgnoreCase)
    {
        "WizardApplyService.cs",
        "HarmonizerApplyService.cs",
        "ProposePlanCommandHandler.cs"
    };

    [Test]
    public void OnlyThePlannerApplyPaths_MaySkipThePlanningPeriodCheck()
    {
        var files = Directory
            .EnumerateFiles(RepositoryRootLocator.ApiProject, SourceFilePattern, SearchOption.AllDirectories)
            .Where(path => !path.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                .Contains(MigrationsDirectory))
            .ToList();
        files.Count.ShouldBeGreaterThan(MinimumScannedFiles, "the scan did not read the real Klacks.Api sources");

        var users = files.Where(path => File.ReadAllText(path).Contains(SkipLiteral)).ToList();
        var violations = users.Where(path => !AllowedFiles.Contains(Path.GetFileName(path))).ToList();

        users.Count.ShouldBeGreaterThan(0, $"no '{SkipLiteral}' found at all - the scan proves nothing");
        var report = new StringBuilder();
        violations.ForEach(path => report.AppendLine($"  {path}"));
        violations.ShouldBeEmpty(
            $"Only {string.Join(", ", AllowedFiles)} may pass {SkipLiteral}; these files do too:{Environment.NewLine}{report}");
    }
}
