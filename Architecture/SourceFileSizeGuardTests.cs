// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Size guard for the Klacks.Api source tree: no file above FileLineLimit lines and no method body above
/// MethodLineLimit lines, unless it is named in one of the two allowlists WITH the line count it had when
/// it was added. The allowlists are ceilings, not exemptions - an allowlisted file that grows fails just
/// like a new offender, so the only way the list can change is downwards.
///
/// A source scan rather than reflection, for the same reason ForbidChallengeSchemeGuardTests scans: the
/// property being measured is a property of the text (lines, a method body), and reflection sees neither.
/// Type declarations are skipped because a file with a block-scoped namespace indents its class by four
/// spaces and is otherwise indistinguishable from a method signature.
///
/// Two properties of the allowlists are deliberate and easy to misread. First, a ceiling encodes what the
/// brace counter measured, not what a human would count: a brace inside a string literal can overstate a
/// body, and the number is still the right ceiling, because it is the number this test will produce
/// again. Second, the method key is "path::name", so overloads and same-named members of one file share
/// ONE ceiling, set by the longest of them - the guard exists to stop growth, not to tell members apart.
/// Introduced 2026-09-16 with the graceful-correction work, after LLMService.cs had reached 1760 lines
/// around a block that belonged in its own service.
/// </summary>

using System.Text.RegularExpressions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class SourceFileSizeGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const int FileLineLimit = 800;
    private const int MethodLineLimit = 150;

    private const string ShrinkOnlyHint =
        "This allowlist may only LOSE entries. Do not add a line and do not raise a ceiling: split the " +
        "file or the method instead. If a split is genuinely impossible, the owner decides - not the test.";

    // .claude is excluded because Klacks.Api\.claude\worktrees\ holds gitignored symlinks to entirely
    // separate sibling repositories (e.g. Klacks.ScheduleOptimizer), kept there only for agent
    // convenience. Directory.EnumerateFiles follows them, so without this exclusion the guard would
    // police another repository's source under Klacks.Api's own ceilings - added 2026-09-16 after the
    // first run surfaced TokenEvolutionLoop.cs and TokenRepair.cs from that symlinked repo.
    private static readonly string[] ExcludedPathFragments =
    [
        $"{Path.DirectorySeparatorChar}Migrations{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}Infrastructure{Path.DirectorySeparatorChar}Persistence{Path.DirectorySeparatorChar}Seed{Path.DirectorySeparatorChar}",
        $"{Path.DirectorySeparatorChar}.claude{Path.DirectorySeparatorChar}"
    ];

    // Relative path -> its measured line count on the day it was allowlisted. See ShrinkOnlyHint.
    private static readonly IReadOnlyDictionary<string, int> FileCeilings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        [@"Infrastructure\Extensions\ServiceCollectionExtensions.cs"] = 1394,
        [@"Infrastructure\Services\ScheduleTimelineBackgroundService.cs"] = 886,
        [@"Application\Services\Assistant\SkillToolsetAssembler.cs"] = 828,
        [@"Domain\Services\Assistant\LLMService.cs"] = 1667,
        [@"Domain\Services\RouteOptimization\ContainerAutofillService.cs"] = 859,
        [@"Infrastructure\Services\AnalyseScenarios\AnalyseScenarioService.cs"] = 991,
        [@"Infrastructure\Services\Plugins\FeaturePluginService.cs"] = 805,
        [@"Infrastructure\Services\Settings\LanguagePluginContentInstaller.cs"] = 907,
        [@"Infrastructure\Services\Settings\RegionSetupService.cs"] = 3601,
        [@"Application\Services\Assistant\Conditions\AgentConditionActionService.cs"] = 965,
        [@"Application\Services\Assistant\Planning\PlanStepExecutor.cs"] = 865,
    };

    // "RelativePath::MethodName" -> its measured body length on the day it was allowlisted.
    private static readonly IReadOnlyDictionary<string, int> MethodCeilings = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
    {
        [@"Application\Skills\BundleNearbyTimeRangeShiftsIntoContainerSkill.cs::ExecuteAsync"] = 279,
        [@"Application\Skills\CreateContainerTemplateSkill.cs::ExecuteAsync"] = 181,
        [@"Application\Skills\CreateEmployeeSkill.cs::ExecuteAsync"] = 272,
        [@"Application\Skills\CreateShiftSkill.cs::ExecuteAsync"] = 243,
        [@"Application\Skills\CutShiftSkill.cs::ExecuteAsync"] = 153,
        [@"Application\Skills\FillGroupByCriteriaSkill.cs::ExecuteAsync"] = 164,
        [@"Application\Skills\ScheduleRecurringTaskSkill.cs::ExecuteAsync"] = 186,
        [@"Application\Skills\UpdateCalendarSelectionSkill.cs::ExecuteAsync"] = 192,
        [@"Application\Skills\UpdateClientSkill.cs::ExecuteAsync"] = 178,
        [@"Application\Skills\UpdateContractSkill.cs::ExecuteAsync"] = 179,
        [@"Infrastructure\Email\EmailTestService.cs::TestConnectionAsync"] = 219,
        [@"Infrastructure\Exceptions\ErrorHandlingMiddleware.cs::Invoke"] = 286,
        [@"Infrastructure\Extensions\ServiceCollectionExtensions.cs::AddDomainServices"] = 161,
        [@"Infrastructure\Extensions\ServiceCollectionExtensions.cs::AddLLMCoreServices"] = 229,
        [@"Application\Services\Assistant\LLMStreamingOrchestrator.cs::ProcessStreamAsync"] = 176,
        [@"Application\Services\Assistant\SkillToolsetAssembler.cs::AssembleAsync"] = 337,
        [@"Domain\Services\Assistant\LLMService.cs::ProcessStreamAsync"] = 570,
        [@"Domain\Services\Assistant\LLMService.cs::HistoryBudgetFor"] = 369,
        [@"Infrastructure\Services\Assistant\LLMModelSyncService.cs::SyncProviderAsync"] = 174,
        [@"Infrastructure\Services\Schedules\HarmonizerJobRunner.cs::RunJobAsync"] = 181,
        [@"Infrastructure\Services\Schedules\WizardJobRunner.cs::RunJobAsync"] = 206,
        [@"Application\Services\Assistant\Conditions\AgentConditionActionService.cs::RunKindAsync"] = 152,
        [@"Application\Services\Schedules\HolisticHarmonizer\HolisticHarmonizerEngine.cs::RunAsync"] = 294,
        [@"Domain\Services\Assistant\Skills\SkillExecutorService.cs::ExecuteAsync"] = 178,
        [@"Application\Services\Assistant\Evaluation\TurnEval\TurnReplayService.cs::ReplayAsync"] = 170,
    };

    private static readonly Regex MemberSignature = new(
        @"^\s{4}(?:\[[^\]]*\]\s*)?(?:(?:public|private|protected|internal|static|async|override|virtual|sealed|partial|new|extern|unsafe)\s+)+[\w<>\[\],\?\.]+\s+(\w+)\s*\(",
        RegexOptions.Compiled);

    private static readonly Regex TypeDeclaration = new(
        @"\b(class|record|struct|interface|enum)\b", RegexOptions.Compiled);

    /// <summary>
    /// Same walk-up as ForbidChallengeSchemeGuardTests.LocateApiProject, including its failure mode: a
    /// missing project THROWS. An Assert.Ignore here would turn "the repository is not checked out" into
    /// a silent pass, and a size guard that can silently pass is not a guard.
    /// </summary>
    private static string ApiRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (Directory.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate {ApiProjectDirectory} by walking up from the test base directory.");
    }

    private static IEnumerable<string> SourceFiles(string root) =>
        Directory.EnumerateFiles(root, "*.cs", SearchOption.AllDirectories)
            .Where(path => !ExcludedPathFragments.Any(path.Contains))
            .Where(path => !path.EndsWith(".Designer.cs", StringComparison.OrdinalIgnoreCase))
            .Where(path => !path.EndsWith("ModelSnapshot.cs", StringComparison.OrdinalIgnoreCase));

    // The allowlist keys are written once, by a human, in the Windows-style form Path.GetRelativePath
    // produces locally. The backend-tests CI job (Klacks.Api/.github/workflows/tests.yml) runs on
    // ubuntu-latest, where GetRelativePath emits forward slashes - without this normalization every
    // allowlisted entry would silently stop matching on that runner and the guard would flag all of
    // them as new offenders on day one.
    private static string NormalizeSeparators(string path) => path.Replace('/', '\\');

    [Test]
    public void NoSourceFile_ExceedsItsLineCeiling()
    {
        var root = ApiRoot();
        var offenders = new List<string>();

        foreach (var path in SourceFiles(root))
        {
            var relative = NormalizeSeparators(Path.GetRelativePath(root, path));
            var lines = File.ReadAllLines(path).Length;
            var ceiling = FileCeilings.TryGetValue(relative, out var allowed) ? allowed : FileLineLimit;

            if (lines > ceiling)
            {
                offenders.Add($"{relative}: {lines} lines (ceiling {ceiling})");
            }
        }

        offenders.ShouldBeEmpty(
            $"Files above their ceiling:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}" +
            $"{Environment.NewLine}{ShrinkOnlyHint}");
    }

    [Test]
    public void NoMethodBody_ExceedsItsLineCeiling()
    {
        var root = ApiRoot();
        var offenders = new List<string>();

        foreach (var path in SourceFiles(root))
        {
            var relative = NormalizeSeparators(Path.GetRelativePath(root, path));
            foreach (var (method, length) in MethodBodies(File.ReadAllLines(path)))
            {
                var key = $"{relative}::{method}";
                var ceiling = MethodCeilings.TryGetValue(key, out var allowed) ? allowed : MethodLineLimit;

                if (length > ceiling)
                {
                    offenders.Add($"{key}: {length} lines (ceiling {ceiling})");
                }
            }
        }

        offenders.ShouldBeEmpty(
            $"Method bodies above their ceiling:{Environment.NewLine}{string.Join(Environment.NewLine, offenders)}" +
            $"{Environment.NewLine}{ShrinkOnlyHint}");
    }

    /// <summary>
    /// Brace-counting from a member signature to its matching close. Good enough on purpose: a brace
    /// inside a string literal can overstate a body, which errs towards flagging - and a flagged method
    /// is read by a human before anything is allowlisted. An expression-bodied member and an abstract
    /// declaration have no body and are skipped.
    /// </summary>
    private static IEnumerable<(string Method, int Length)> MethodBodies(IReadOnlyList<string> lines)
    {
        for (var i = 0; i < lines.Count; i++)
        {
            var match = MemberSignature.Match(lines[i]);
            if (!match.Success || TypeDeclaration.IsMatch(lines[i]))
            {
                continue;
            }

            if (lines[i].TrimEnd().EndsWith(';') || lines[i].Contains("=>", StringComparison.Ordinal))
            {
                continue;
            }

            var depth = 0;
            var started = false;
            var end = i;

            while (end < lines.Count)
            {
                depth += lines[end].Count(c => c == '{') - lines[end].Count(c => c == '}');
                if (lines[end].Contains('{', StringComparison.Ordinal))
                {
                    started = true;
                }

                if (started && depth <= 0)
                {
                    break;
                }

                end++;
            }

            if (!started || end >= lines.Count)
            {
                continue;
            }

            yield return (match.Groups[1].Value, end - i + 1);
            i = end;
        }
    }
}
