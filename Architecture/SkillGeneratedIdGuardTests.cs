// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Ids belong to the server. A skill that mints a Guid and then reports it has no way of knowing whether
/// the row ended up carrying it: Groups/PostCommandHandler, and five sibling handlers, overwrite the id of
/// every posted resource with a fresh one. create_group did exactly that until 2026-09-21 and every
/// follow-up that used the reported id addressed a row that does not exist ("Parent group ... not found",
/// 404). The fix is always the same shape - post without an id, read the persisted one back off the
/// response - and this guard stops the pattern from coming back somewhere else.
///
/// The allowlist is a ceiling per file, exactly like SourceFileSizeGuardTests: it may only LOSE entries
/// and no count may rise. A new Guid.NewGuid() in a skill therefore fails until someone has decided which
/// of the three legitimate cases it is, and written it down:
/// (1) not an entity id at all - a job, correlation or lock token that never reaches a database row;
/// (2) an id the skill must know BEFORE the write because it wires a child to its parent in the same
///     payload, on a route whose handler keeps what it is given;
/// (3) a row written straight through a repository, where no handler re-assigns anything.
/// None of the three may report the minted value as the created row's id without reading it back.
/// </summary>

using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class SkillGeneratedIdGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string SkillsRelativeDirectory = @"Application\Skills";
    private const string GeneratedIdCall = "Guid.NewGuid()";

    private const string ShrinkOnlyHint =
        "This allowlist may only LOSE entries and no count may rise. A skill must not mint the id of a " +
        "row it creates: post without one and read the persisted id off the response, the way " +
        "CreateGroupSkill and CreateEmployeeSkill do.";

    // File (relative to Klacks.Api) -> occurrences on the day it was allowlisted, and why they are there.
    private static readonly IReadOnlyDictionary<string, int> Allowed =
        new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase)
        {
            // Case (3): agent memory and pending notes are written through their own repositories.
            [@"Application\Skills\AddAiMemorySkill.cs"] = 1,
            [@"Application\Skills\AddPersonalMemorySkill.cs"] = 1,
            [@"Application\Skills\StashPendingNoteSkill.cs"] = 1,

            // Case (2)/(3): link and schedule rows posted or added on routes whose handlers keep the id
            // they are given; none of these skills reports the minted value as the created row's id.
            [@"Application\Skills\AddBreakPlaceholderSkill.cs"] = 1,
            [@"Application\Skills\AddClientToGroupSkill.cs"] = 1,
            [@"Application\Skills\AddClientToNearestGroupSkill.cs"] = 1,
            [@"Application\Skills\AddScheduleCommandSkill.cs"] = 1,
            [@"Application\Skills\AddScheduleCommandsRangeSkill.cs"] = 1,
            [@"Application\Skills\AddScheduleNoteSkill.cs"] = 1,
            [@"Application\Skills\AddShiftToGroupSkill.cs"] = 1,
            [@"Application\Skills\AssignContractByNameSkill.cs"] = 1,
            [@"Application\Skills\AssignContractToClientSkill.cs"] = 1,
            [@"Application\Skills\BulkAddShiftsToGroupSkill.cs"] = 1,
            [@"Application\Skills\CreateAbsenceSkill.cs"] = 1,
            [@"Application\Skills\CreateAbsenceTypeSkill.cs"] = 1,
            [@"Application\Skills\CreateAddressSkill.cs"] = 1,
            [@"Application\Skills\CreateShiftSkill.cs"] = 1,
            [@"Application\Skills\ImportCalendarRulesSkill.cs"] = 1,
            [@"Application\Skills\Base\SettingsWriterSkillBase.cs"] = 1,
            [@"Application\Skills\SetErpImportScheduleSkill.cs"] = 1,
            [@"Application\Skills\UpdateOwnerLocaleSettingsSkill.cs"] = 1,
            [@"Application\Skills\UpdateSpamFilterSettingsSkill.cs"] = 1,
            [@"Application\Skills\UpdateWebSearchSettingsSkill.cs"] = 1,

            // Case (2): a parent and its children travel in ONE payload, so the children need the
            // parent's id before the write. CreateEmployeeSkill reads the persisted id back afterwards
            // (client.Id = result.Value.Id) and reports that one.
            [@"Application\Skills\BundleNearbyTimeRangeShiftsIntoContainerSkill.cs"] = 1,
            [@"Application\Skills\CreateEmployeeSkill.cs"] = 5,
            [@"Application\Skills\CreateTestEnvironmentSkill.cs"] = 7,
            [@"Application\Skills\UpdateClientSkill.cs"] = 3,

            // Case (2): the cut addresses the new piece by id on a route that keeps it.
            [@"Application\Skills\CutShiftByDateSkill.cs"] = 1,
            [@"Application\Skills\CutShiftSkill.cs"] = 1,

            // Case (1): a background job handle, never a database row.
            [@"Application\Skills\SealOpenOrdersSkill.cs"] = 1,
        };

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

    private static string NormalizeSeparators(string path) => path.Replace('/', '\\');

    [Test]
    public void NoSkill_MintsMoreEntityIdsThanItsAllowlistedCount()
    {
        var root = ApiRoot();
        var skills = Path.Combine(root, SkillsRelativeDirectory);
        var offenders = new List<string>();

        foreach (var path in Directory.EnumerateFiles(skills, "*.cs", SearchOption.AllDirectories))
        {
            var relative = NormalizeSeparators(Path.GetRelativePath(root, path));
            var count = File.ReadAllLines(path)
                .Count(line => line.Contains(GeneratedIdCall, StringComparison.Ordinal));
            var ceiling = Allowed.TryGetValue(relative, out var allowed) ? allowed : 0;

            if (count > ceiling)
            {
                offenders.Add($"{relative}: {count} occurrence(s) of {GeneratedIdCall} (ceiling {ceiling})");
            }
        }

        offenders.ShouldBeEmpty(
            $"Skills minting ids above their ceiling:{Environment.NewLine}" +
            $"{string.Join(Environment.NewLine, offenders)}{Environment.NewLine}{ShrinkOnlyHint}");
    }

    // The other direction: an allowlist entry that no longer matches reality is a stale exemption, and a
    // ceiling that was silently lowered by a fix has to come down here too.
    [Test]
    public void TheAllowlist_HasNoStaleEntries()
    {
        var root = ApiRoot();
        var stale = new List<string>();

        foreach (var (relative, ceiling) in Allowed)
        {
            var path = Path.Combine(root, relative);
            var count = File.Exists(path)
                ? File.ReadAllLines(path).Count(line => line.Contains(GeneratedIdCall, StringComparison.Ordinal))
                : 0;

            if (count < ceiling)
            {
                stale.Add($"{relative}: allowlisted for {ceiling}, actually {count}");
            }
        }

        stale.ShouldBeEmpty(
            $"Allowlist entries above what the code still contains:{Environment.NewLine}" +
            $"{string.Join(Environment.NewLine, stale)}{Environment.NewLine}" +
            "Lower or remove the entry - the list only moves downwards.");
    }
}
