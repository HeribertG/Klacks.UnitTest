// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Architecture guard for the binding of a confirmation to its server preview. (a) SkillExecutionContext
/// .ConfirmedActionBinding is the proof that a user confirmed exactly what the preview showed; it may be assigned only by
/// confirm_pending_action, from the row it redeemed. Any other assignment (a background runner, a plan step, a helper) would
/// let a skill run on a binding nobody confirmed. This is a source scan of Klacks.Api: the violation is an assignment inside
/// a method body, which reflection cannot see. (b) Every skill a registered preview provider supports refuses a call with
/// no binding in its context with the BindingRequired text and touches none of its dependencies, also when the autonomy gate
/// is bypassed (a plan step); this covers the skills of the messaging plugin too (send_message), which receive a reduced
/// context and refuse with the same shared text. The skills are built with substitutes for their dependencies; a skill a new
/// provider supports must be given valid parameters in ParametersFor, otherwise the guard fails and says so (the refusal for a
/// missing binding comes after the role and parameter checks in the macro skills, so it can only be proven with valid
/// parameters). (c) The plugin contract carries the binding as ConfirmedBinding: the plugin sources (Klacks.Plugin.Messaging,
/// Klacks.Plugin.Contracts) never assign it, and in Klacks.Api only SkillExecutorService does, copying ConfirmedActionBinding of
/// the host context.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Skills.Implementations;
using Klacks.Plugin.Messaging.Application.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ConfirmedBindingPinGuardTests
{
    private const string AssignmentPattern = @"\bConfirmedActionBinding\s*=(?![=>])";
    private const string PluginContextAssignmentPattern = @"\bConfirmedBinding\s*=(?![=>])";
    private const string PluginMessagingDirectory = "Klacks.Plugin.Messaging";
    private const string PluginContractsDirectory = "Klacks.Plugin.Contracts";
    private const int MinimumScannedPluginFiles = 20;
    private const string SourceFilePattern = "*.cs";
    private const string LineCommentPrefix = "//";
    private const string MigrationsDirectory = "Migrations";
    private const int MinimumScannedFiles = 500;

    private static readonly string[] AllowedAssignmentFile =
        ["Application", "Skills", "ConfirmPendingActionSkill.cs"];

    private static readonly string[] AllowedPluginContextAssignmentFile =
        ["Domain", "Services", "Assistant", "Skills", "SkillExecutorService.cs"];

    private static readonly Regex Assignment = new(AssignmentPattern, RegexOptions.Compiled);
    private static readonly Regex PluginContextAssignment = new(PluginContextAssignmentPattern, RegexOptions.Compiled);

    private static Dictionary<string, object>? ParametersFor(string skillName) => skillName switch
    {
        BulkDeleteWorksSkill.SkillName => new Dictionary<string, object>
        {
            ["firstName"] = "Max",
            ["lastName"] = "Muster",
            ["fromDate"] = "2026-11-01"
        },
        CutShiftSkill.SkillName => new Dictionary<string, object>
        {
            [CutShiftSkill.ShiftIdParameter] = Guid.NewGuid().ToString(),
            [CutShiftSkill.PartsParameter] = "07:00-15:00,15:00-07:00"
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
        SealOpenOrdersSkill.SkillName => new Dictionary<string, object>
        {
            [PreviewApplySkillCalls.ApplyParameter] = true
        },
        SealShiftSkill.SkillName => new Dictionary<string, object>
        {
            [SealShiftSkill.ShiftIdParameter] = Guid.NewGuid().ToString()
        },
        MessagingConstants.SkillSendMessage => new Dictionary<string, object>
        {
            ["provider"] = "telegram",
            ["recipient"] = "Hans Muster",
            ["content"] = "Hello"
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
    public void ThePluginContextBinding_IsAssignedOnlyWhereTheHostCopiesItsOwn()
    {
        var apiRoot = RepositoryRootLocator.ApiProject;
        var allowed = Path.GetFullPath(Path.Combine([apiRoot, .. AllowedPluginContextAssignmentFile]));
        var violations = new List<string>();
        var allowedAssignments = 0;

        foreach (var file in SourcesOf(apiRoot))
        {
            foreach (var line in AssignmentLines(file, PluginContextAssignment))
            {
                if (string.Equals(Path.GetFullPath(file), allowed, StringComparison.OrdinalIgnoreCase))
                {
                    allowedAssignments++;
                }
                else
                {
                    violations.Add($"{Path.GetRelativePath(apiRoot, file)}:{line}");
                }
            }
        }

        allowedAssignments.ShouldBeGreaterThan(0, "SkillExecutorService no longer copies the binding to the plugin context; the scan is vacuous.");
        violations.ShouldBeEmpty(
            "The plugin context binding may be assigned only in SkillExecutorService, from ConfirmedActionBinding: "
            + string.Join(", ", violations));
    }

    [TestCase(PluginMessagingDirectory)]
    [TestCase(PluginContractsDirectory)]
    public void ThePluginSources_NeverAssignABindingToAContext(string pluginDirectory)
    {
        var root = RepositoryRootLocator.FindDirectory(pluginDirectory);
        root.ShouldNotBeNull($"{pluginDirectory} is not next to Klacks.Api; the scan cannot cover it.");
        var scanned = 0;
        var violations = new List<string>();

        foreach (var file in SourcesOf(root))
        {
            scanned++;
            foreach (var pattern in new[] { Assignment, PluginContextAssignment })
            {
                violations.AddRange(AssignmentLines(file, pattern).Select(line => $"{Path.GetRelativePath(root, file)}:{line}"));
            }
        }

        scanned.ShouldBeGreaterThan(pluginDirectory == PluginMessagingDirectory ? MinimumScannedPluginFiles : 0, "The scan read no real plugin sources.");
        violations.ShouldBeEmpty(
            $"{pluginDirectory} must never set a confirmed binding on a context; only the host does: " + string.Join(", ", violations));
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
            var (success, message) = ConfirmationPreviewProviderFixtures.IsPluginSkill(type)
                ? await RunPluginSkillWithoutBindingAsync((Klacks.Plugin.Contracts.Skills.ISkillImplementation)constructor.Invoke(dependencies), parameters)
                : await RunSkillWithoutBindingAsync((BaseSkillImplementation)constructor.Invoke(dependencies), parameters);

            success.ShouldBeFalse($"'{skillName}' ran without a confirmed binding.");
            message.ShouldBe(
                string.Format(ConfirmationBindingMessages.BindingRequired, skillName),
                $"'{skillName}' did not refuse with BindingRequired.");
            foreach (var dependency in dependencies)
            {
                dependency.ReceivedCalls().ShouldBeEmpty(
                    $"'{skillName}' used {dependency.GetType().BaseType?.Name ?? "a dependency"} before it checked the binding.");
            }
        }
    }

    private static async Task<(bool Success, string? Message)> RunSkillWithoutBindingAsync(
        BaseSkillImplementation skill, Dictionary<string, object> parameters)
    {
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
        return (result.Success, result.Message);
    }

    private static async Task<(bool Success, string? Message)> RunPluginSkillWithoutBindingAsync(
        Klacks.Plugin.Contracts.Skills.ISkillImplementation skill, Dictionary<string, object> parameters)
    {
        var context = new Klacks.Plugin.Contracts.Skills.SkillExecutionContext
        {
            UserId = Guid.NewGuid(),
            TenantId = Guid.NewGuid(),
            UserName = "guard",
            UserPermissions = new List<string> { Roles.Admin }
        };

        var result = await skill.ExecuteAsync(context, parameters);
        return (result.Success, result.Message);
    }

    private static IEnumerable<string> SourcesOf(string root) =>
        Directory.EnumerateFiles(root, SourceFilePattern, SearchOption.AllDirectories)
            .Where(file =>
            {
                var relative = Path.GetRelativePath(root, file);
                return !relative.Contains(MigrationsDirectory) && !IsBuildOutput(relative);
            });

    private static IEnumerable<int> AssignmentLines(string file, Regex pattern)
    {
        var lines = File.ReadAllLines(file);
        for (var index = 0; index < lines.Length; index++)
        {
            if (!lines[index].TrimStart().StartsWith(LineCommentPrefix, StringComparison.Ordinal) && pattern.IsMatch(lines[index]))
            {
                yield return index + 1;
            }
        }
    }

    private static bool IsBuildOutput(string relativePath) =>
        relativePath.StartsWith("bin" + Path.DirectorySeparatorChar, StringComparison.Ordinal)
        || relativePath.StartsWith("obj" + Path.DirectorySeparatorChar, StringComparison.Ordinal);
}
