// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Architecture guard for the binding of a confirmation to its server preview. (a) SkillExecutionContext
/// .ConfirmedActionBinding is the proof that a user confirmed exactly what the preview showed; it may be assigned only by
/// confirm_pending_action, from the row it redeemed. Any other assignment (a background runner, a plan step, a helper) would
/// let a skill run on a binding nobody confirmed. This is a source scan of Klacks.Api: the violation is an assignment inside
/// a method body, which reflection cannot see. (b) Every skill a registered preview provider supports refuses a call with
/// no binding in its context with the BindingRequired text and touches none of its dependencies, also when the autonomy gate
/// is bypassed (a plan step). The skills are built with substitutes for their dependencies; a skill a new provider supports
/// must be given valid parameters in ParametersFor, otherwise the guard fails and says so (the refusal for a missing
/// binding comes after the role and parameter checks in the macro skills, so it can only be proven with valid parameters).
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills.Implementations;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ConfirmedBindingPinGuardTests
{
    private const string AssignmentPattern = @"\bConfirmedActionBinding\s*=(?![=>])";
    private const string SourceFilePattern = "*.cs";
    private const string LineCommentPrefix = "//";
    private const string MigrationsDirectory = "Migrations";
    private const int MinimumScannedFiles = 500;

    private static readonly string[] AllowedAssignmentFile =
        ["Application", "Skills", "ConfirmPendingActionSkill.cs"];

    private static readonly Regex Assignment = new(AssignmentPattern, RegexOptions.Compiled);

    private static Dictionary<string, object>? ParametersFor(string skillName) => skillName switch
    {
        BulkDeleteWorksSkill.SkillName => new Dictionary<string, object>
        {
            ["firstName"] = "Max",
            ["lastName"] = "Muster",
            ["fromDate"] = "2026-11-01"
        },
        MacroAssignmentSkillNames.AssignToShift => new Dictionary<string, object>
        {
            [MacroAssignmentParameters.ShiftId] = Guid.NewGuid().ToString(),
            [MacroAssignmentParameters.MacroId] = Guid.NewGuid().ToString()
        },
        MacroAssignmentSkillNames.AssignToAbsenceType => new Dictionary<string, object>
        {
            [MacroAssignmentParameters.AbsenceTypeId] = Guid.NewGuid().ToString(),
            [MacroAssignmentParameters.MacroId] = Guid.NewGuid().ToString()
        },
        MacroAssignmentSkillNames.Revert => new Dictionary<string, object>
        {
            [MacroAssignmentParameters.SwitchId] = Guid.NewGuid().ToString()
        },
        _ => null
    };

    [Test]
    public void TheConfirmedBinding_IsAssignedOnlyByTheConfirmSkill()
    {
        var apiRoot = RepositoryRootLocator.ApiProject;
        var allowed = Path.GetFullPath(Path.Combine([apiRoot, .. AllowedAssignmentFile]));
        var scanned = 0;
        var violations = new List<string>();
        var allowedAssignments = 0;

        foreach (var file in Directory.EnumerateFiles(apiRoot, SourceFilePattern, SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(apiRoot, file);
            if (relative.Contains(MigrationsDirectory) || IsBuildOutput(relative))
            {
                continue;
            }

            scanned++;
            var lines = File.ReadAllLines(file);
            for (var index = 0; index < lines.Length; index++)
            {
                if (lines[index].TrimStart().StartsWith(LineCommentPrefix, StringComparison.Ordinal)
                    || !Assignment.IsMatch(lines[index]))
                {
                    continue;
                }

                if (string.Equals(Path.GetFullPath(file), allowed, StringComparison.OrdinalIgnoreCase))
                {
                    allowedAssignments++;
                }
                else
                {
                    violations.Add($"{relative}:{index + 1}");
                }
            }
        }

        scanned.ShouldBeGreaterThan(MinimumScannedFiles, "The scan cannot have read the real Klacks.Api sources.");
        allowedAssignments.ShouldBeGreaterThan(0, "confirm_pending_action no longer assigns the binding; the scan is vacuous.");
        violations.ShouldBeEmpty(
            "ConfirmedActionBinding may be assigned only in ConfirmPendingActionSkill, from the redeemed row: "
            + string.Join(", ", violations));
    }

    [Test]
    public async Task EverySkillAPreviewProviderSupports_RefusesWithoutABinding_AndTouchesNothing()
    {
        var supported = ConfirmationPreviewProviderFixtures.SupportedSkills().Select(entry => entry.SkillName).Distinct().ToList();
        supported.ShouldNotBeEmpty();

        foreach (var skillName in supported)
        {
            var parameters = ParametersFor(skillName);
            parameters.ShouldNotBeNull(
                $"'{skillName}' is now supported by a preview provider: add valid parameters for it to ParametersFor so the "
                + "guard can prove that it refuses without a binding.");

            var type = ConfirmationPreviewProviderFixtures.ImplementationOf(skillName);
            var constructor = type.GetConstructors().Single();
            var dependencies = constructor.GetParameters()
                .Select(parameter => Substitute.For([parameter.ParameterType], []))
                .ToArray();
            var skill = (BaseSkillImplementation)constructor.Invoke(dependencies);
            var context = new SkillExecutionContext
            {
                UserId = Guid.NewGuid(),
                TenantId = Guid.NewGuid(),
                UserName = "guard",
                UserLanguage = "en",
                UserPermissions = new List<string> { Roles.Admin },
                BypassAutonomyGate = true
            };

            var result = await skill.ExecuteAsync(context, parameters);

            result.Success.ShouldBeFalse($"'{skillName}' ran without a confirmed binding.");
            result.Message.ShouldBe(
                string.Format(ConfirmationBindingMessages.BindingRequired, skillName),
                $"'{skillName}' did not refuse with BindingRequired.");
            foreach (var dependency in dependencies)
            {
                dependency.ReceivedCalls().ShouldBeEmpty(
                    $"'{skillName}' used {dependency.GetType().BaseType?.Name ?? "a dependency"} before it checked the binding.");
            }
        }
    }

    private static bool IsBuildOutput(string relativePath) =>
        relativePath.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || relativePath.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
