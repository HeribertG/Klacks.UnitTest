// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Architecture guard for I2 of the Klacksy-autonomy test specification (docs/knowledge/klacksy-
/// autonomie-testspezifikation-2026-08-28.md, §2): the seven proactive-trigger files that used to read
/// the system clock directly via DateTime.UtcNow were migrated to the injected TimeProvider pattern
/// (see AgentConditionActionService and AgentConditionLedgerService, which established it), so their
/// day-key derivation, snooze-expiry checks and timestamps are testable with a fake clock instead of
/// racing the real one. AgentTriggerService joined the guarded set later (package F): it stamps the
/// reminder due dates from the injected clock. This guard fails if a direct system-clock read
/// (DateTime.UtcNow, DateTimeOffset.UtcNow, DateTime.Now, DateTime.Today) ever creeps back into any of
/// the guarded files.
///
/// A source scan is used deliberately, mirroring ForbidChallengeSchemeGuardTests: the violation is an
/// expression inside a method body, and reflection sees only signatures and attributes, not what a
/// property getter or method does internally.
///
/// Deliberately scoped to these files only, not the whole repository: most of Klacks.Api still
/// reads DateTime.UtcNow directly, and that is explicitly out of scope for I2 - a whole-repository scan
/// would fail for reasons this task never touched.
///
/// Scope note — what this guard does NOT cover:
/// - Whitespace or comment variants such as "DateTime . UtcNow" or "DateTime/*x*/.UtcNow". The literal
///   is matched verbatim, so a deliberately reformatted access slips through.
/// - Any other file in the repository that still uses DateTime.UtcNow. Only the files listed in
///   GuardedRelativeFiles are guarded; a repository-wide guard is a separate, larger effort.
/// </summary>

using System.Text;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ProactiveTimeProviderGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string ApiProjectMarkerDirectory = "Application";
    private const string LineCommentPrefix = "//";

    // Every direct read of the system clock, not just DateTime.UtcNow: DateTimeOffset.UtcNow,
    // DateTime.Now and DateTime.Today race the real clock in exactly the same way.
    private static readonly string[] ForbiddenPatterns =
    [
        "DateTime.UtcNow",
        "DateTimeOffset.UtcNow",
        "DateTime.Now",
        "DateTime.Today",
    ];

    private static readonly string[] GuardedRelativeFiles =
    [
        "Application/Services/Assistant/Triggers/AgentTriggerRateLimiter.cs",
        "Application/Services/Assistant/Triggers/PersistentAgentTriggerPreferenceService.cs",
        "Infrastructure/Repositories/Assistant/ProactiveTriggerDispatchRepository.cs",
        "Application/Services/Assistant/Triggers/InMemoryAgentTriggerPreferenceService.cs",
        "Application/Services/Assistant/Triggers/EmptyContainerTriggerEvent.cs",
        "Application/Services/Assistant/Triggers/UserActivityTracker.cs",
        "Application/Handlers/Assistant/SetProactiveReactionCommandHandler.cs",
        // Joined the guarded set with package F: it stamps the first reminder due date from the
        // injected TimeProvider and must never fall back to the system clock.
        "Application/Services/Assistant/Triggers/AgentTriggerService.cs",
        // Joined the guarded set with package F (B5): the reminder sweep stamps reminded-at and the
        // next due date from the injected TimeProvider.
        "Application/Services/Assistant/Triggers/ProactiveReminderService.cs",
    ];

    [Test]
    public void GuardedProactiveFiles_MustNotReadTheSystemClock()
    {
        var apiRoot = LocateApiProject();
        var violations = new List<(string File, int Line)>();
        var scannedFiles = 0;

        foreach (var relativeFile in GuardedRelativeFiles)
        {
            var absoluteFile = Path.Combine(apiRoot, relativeFile.Replace('/', Path.DirectorySeparatorChar));

            if (!File.Exists(absoluteFile))
            {
                throw new FileNotFoundException(
                    $"Guarded file '{relativeFile}' does not exist under '{apiRoot}'. The guard would " +
                    "silently pass, so a missing file is treated as a failure.");
            }

            scannedFiles++;
            var lines = File.ReadAllLines(absoluteFile);

            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith(LineCommentPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                foreach (var pattern in ForbiddenPatterns)
                {
                    if (line.Contains(pattern, StringComparison.Ordinal))
                    {
                        violations.Add((relativeFile, i + 1));
                        break;
                    }
                }
            }
        }

        scannedFiles.ShouldBe(
            GuardedRelativeFiles.Length,
            "Not every guarded file was scanned; the guard cannot have inspected the full set.");

        var report = new StringBuilder();
        foreach (var violation in violations)
        {
            report.AppendLine($"  {violation.File}:{violation.Line}");
        }

        violations.ShouldBeEmpty(
            "A direct system-clock read (DateTime.UtcNow/DateTimeOffset.UtcNow/DateTime.Now/" +
            "DateTime.Today) reappeared in a file I2 migrated to the injected TimeProvider pattern. " +
            "Use the injected TimeProvider (_timeProvider.GetUtcNow().UtcDateTime) instead, so the " +
            $"clock stays testable.{Environment.NewLine}{report}");
    }

    private static string LocateApiProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (Directory.Exists(Path.Combine(candidate, ApiProjectMarkerDirectory)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the {ApiProjectDirectory} project by walking up from the test base directory.");
    }
}
