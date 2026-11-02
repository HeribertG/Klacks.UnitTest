// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Architecture guard over the embedded SQL files in Klacks.Api/Infrastructure/Persistence/StoredProcedures.
/// StoredProcedureInitializer executes every embedded .sql file of that folder on EVERY backend start, so
/// a file there must be safe to re-run: no top-level DO block and no UPDATE or DELETE on analyse_scenarios, the
/// two constructs this guard checks. cleanup_scenarios_before_scenarioSourceShiftId.sql was a one-off script (a DO block that
/// soft-deletes all analyse_scenarios with their works, breaks and clones); it wiped every scenario on each
/// start from 2026-05-10 on. One-off cleanups belong in an EF migration, which runs exactly once.
/// The scan removes SQL comments first, so a comment may still mention the forbidden constructs, and
/// treats a DO block at the start of a line and any UPDATE/DELETE on analyse_scenarios as violations.
/// There is no allowlist.
/// </summary>

using System.Text;
using System.Text.RegularExpressions;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class StartupSqlScriptsGuardTests
{
    private const string StoredProceduresDirectory = "Infrastructure/Persistence/StoredProcedures";
    private const string SqlFilePattern = "*.sql";
    private const int MinimumScannedFiles = 3;

    private const string ScannerSelfTestSample =
        "-- DO $$ in a comment is fine\n"
        + "/* UPDATE analyse_scenarios in a block comment is fine */\n"
        + "CREATE OR REPLACE FUNCTION f() RETURNS int AS $$ SELECT 1 $$ LANGUAGE sql;\n"
        + "  DO $$\n"
        + "BEGIN END $$;\n"
        + "update  analyse_scenarios SET is_deleted = TRUE;\n"
        + "DELETE\tFROM analyse_scenarios;\n";

    private static readonly Regex TopLevelDoBlock =
        new(@"(?mi)^\s*DO\s+\$", RegexOptions.Compiled);

    private static readonly Regex ScenarioMutation =
        new(@"(?i)\b(UPDATE\s+analyse_scenarios|DELETE\s+FROM\s+analyse_scenarios)\b", RegexOptions.Compiled);

    private static readonly Regex LineComment =
        new(@"--[^\r\n]*", RegexOptions.Compiled);

    private static readonly Regex BlockComment =
        new(@"/\*.*?\*/", RegexOptions.Compiled | RegexOptions.Singleline);

    [Test]
    public void StartupSqlScripts_MustNotContainOneOffCleanups()
    {
        var apiRoot = RepositoryRootLocator.ApiProject;
        var directory = Path.Combine(apiRoot, StoredProceduresDirectory);

        if (!Directory.Exists(directory))
        {
            throw new DirectoryNotFoundException(
                $"Guarded directory '{StoredProceduresDirectory}' does not exist under '{apiRoot}'. " +
                "The guard would silently pass, so this is treated as a failure.");
        }

        var files = Directory.EnumerateFiles(directory, SqlFilePattern, SearchOption.TopDirectoryOnly).ToList();

        files.Count.ShouldBeGreaterThanOrEqualTo(
            MinimumScannedFiles,
            $"Only {files.Count} .sql files were found in {StoredProceduresDirectory}. The guard cannot " +
            "have inspected the real stored procedure folder, so a green result would be meaningless.");

        var report = new StringBuilder();
        foreach (var file in files.OrderBy(f => f, StringComparer.Ordinal))
        {
            var name = Path.GetFileName(file);
            foreach (var violation in FindViolations(File.ReadAllText(file)))
            {
                report.AppendLine($"  {name}: {violation}");
            }
        }

        report.ToString().ShouldBeEmpty(
            "Every .sql file in the StoredProcedures folder runs on EVERY backend start " +
            "(StoredProcedureInitializer), so it must be safe to re-run. A top-level DO block or " +
            "an UPDATE/DELETE on analyse_scenarios is a one-off data cleanup that would re-run on each " +
            "start and destroy data again. Move one-off cleanups into an EF migration, which runs exactly " +
            $"once.{Environment.NewLine}{report}");
    }

    [Test]
    public void Scanner_FlagsDoBlockAndScenarioMutation_ButIgnoresComments()
    {
        var violations = FindViolations(ScannerSelfTestSample);

        violations.Count.ShouldBe(3, string.Join(" | ", violations));
        violations.Count(v => v.StartsWith("DO block", StringComparison.Ordinal)).ShouldBe(1);
        violations.Count(v => v.StartsWith("analyse_scenarios mutation", StringComparison.Ordinal)).ShouldBe(2);
    }

    private static List<string> FindViolations(string sql)
    {
        var stripped = LineComment.Replace(BlockComment.Replace(sql, string.Empty), string.Empty);
        var violations = new List<string>();

        foreach (Match match in TopLevelDoBlock.Matches(stripped))
        {
            violations.Add($"DO block at top level: '{match.Value.Trim()}'");
        }

        foreach (Match match in ScenarioMutation.Matches(stripped))
        {
            violations.Add($"analyse_scenarios mutation: '{match.Value}'");
        }

        return violations;
    }
}
