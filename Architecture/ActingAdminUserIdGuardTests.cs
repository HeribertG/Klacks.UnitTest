// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Source-scan guard, in the style of ForbidChallengeSchemeGuardTests: ClosePeriodByGroupCommand.ActingAdminUserId
/// lets a caller without HttpContext seal periods as an admin, so exactly one place may set it - the autonomous
/// period close (PeriodAutoCloseService), which takes the id from IPeriodAutoCloseResolver. Every other write is
/// a violation, with two spelled-out exceptions: the optional parameter declaration of the command itself and
/// the controller clearing the field (ActingAdminUserId = null). The command may also only be constructed in
/// the files listed below, so a positional seventh argument cannot slip past the named-argument scan.
/// A source scan because the violation is an argument inside a method body, which reflection cannot see.
/// Not covered: target-typed construction (new(...)) of the command, deserialisation paths other than the
/// controller (the property is [JsonIgnore]), and projects other than Klacks.Api.
/// </summary>

using System.Text;
using System.Text.RegularExpressions;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ActingAdminUserIdGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string SourceFilePattern = "*.cs";
    private const string LineCommentPrefix = "//";
    private const int MinimumScannedFiles = 500;
    private const string AutoCloseServiceFile = "Application/Services/Assistant/Triggers/PeriodAutoCloseService.cs";
    private const string CommandFile = "Application/Commands/PeriodClosing/ClosePeriodByGroupCommand.cs";
    private const string ControllerFile = "Presentation/Controllers/UserBackend/PeriodClosing/PeriodClosingController.cs";
    private const string ClosePeriodSkillFile = "Application/Skills/ClosePeriodSkill.cs";
    private const string ClearingAssignment = "ActingAdminUserId = null";
    private const string CommandConstruction = "new ClosePeriodByGroupCommand(";

    private static readonly string[] ExcludedSegments = ["/obj/", "/bin/", "/Migrations/"];

    private static readonly Regex ActingAdminWrite =
        new(@"\bActingAdminUserId\s*(:|=(?!=))", RegexOptions.Compiled);

    private static readonly HashSet<string> FilesAllowedToConstructTheCommand = new(StringComparer.Ordinal)
    {
        AutoCloseServiceFile,
        ClosePeriodSkillFile
    };

    [Test]
    public void ActingAdminUserId_IsOnlySetByTheAutonomousPeriodClose()
    {
        var scan = Scan();

        scan.ScannedFiles.ShouldBeGreaterThan(MinimumScannedFiles, "The guard did not read the real Api sources.");
        scan.AllowedWrites.ShouldBe(1, "The one legitimate write in PeriodAutoCloseService was not found - the scan proves nothing.");

        scan.Violations.ShouldBeEmpty(
            "ActingAdminUserId lets a background caller seal periods as an admin. Only PeriodAutoCloseService may "
            + "set it (with the admin resolved by IPeriodAutoCloseResolver):" + Environment.NewLine
            + string.Join(Environment.NewLine, scan.Violations));
    }

    [Test]
    public void ClosePeriodByGroupCommand_IsOnlyConstructedInKnownPlaces()
    {
        var scan = Scan();

        scan.Constructions.ShouldNotBeEmpty("No construction of the command was found - the scan proves nothing.");

        scan.Constructions
            .Where(file => !FilesAllowedToConstructTheCommand.Contains(file))
            .ShouldBeEmpty(
                "A new place constructs ClosePeriodByGroupCommand. Check that it never passes ActingAdminUserId "
                + "(also not positionally) and add the file to FilesAllowedToConstructTheCommand deliberately.");
    }

    private static ScanResult Scan()
    {
        var apiRoot = LocateApiProject();
        var violations = new List<string>();
        var constructions = new HashSet<string>(StringComparer.Ordinal);
        var allowedWrites = 0;
        var scannedFiles = 0;

        foreach (var file in Directory.EnumerateFiles(apiRoot, SourceFilePattern, SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(apiRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (ExcludedSegments.Any(segment => ("/" + relative).Contains(segment, StringComparison.Ordinal)))
            {
                continue;
            }

            scannedFiles++;
            var lines = File.ReadAllLines(file, Encoding.UTF8);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                if (line.TrimStart().StartsWith(LineCommentPrefix, StringComparison.Ordinal))
                {
                    continue;
                }

                if (line.Contains(CommandConstruction, StringComparison.Ordinal))
                {
                    constructions.Add(relative);
                }

                if (!ActingAdminWrite.IsMatch(line))
                {
                    continue;
                }

                if (relative == AutoCloseServiceFile)
                {
                    allowedWrites++;
                    continue;
                }

                var isDeclaration = relative == CommandFile;
                var isClearing = relative == ControllerFile && line.Contains(ClearingAssignment, StringComparison.Ordinal);
                if (!isDeclaration && !isClearing)
                {
                    violations.Add($"  {relative}:{i + 1} -> {line.Trim()}");
                }
            }
        }

        return new ScanResult(scannedFiles, allowedWrites, violations, constructions);
    }

    private static string LocateApiProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (File.Exists(Path.Combine(candidate, ApiProjectDirectory + ".csproj")))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the {ApiProjectDirectory} project by walking up from the test base directory.");
    }

    private sealed record ScanResult(
        int ScannedFiles, int AllowedWrites, List<string> Violations, HashSet<string> Constructions);
}
