// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins who may dispatch the Shift put and delete commands, because those two handlers apply the lock for shifts that are
/// no longer draft orders (LockedShiftEditPolicy). The legitimate writers of such shifts - the cut editor and cut reset,
/// order supersession, sealing, the set-end-date skill, the ERP import, the city-group assignment and the scenario
/// accept - write through the shift repository or the database context and must keep doing so, because routing one of them
/// through a command would turn its normal work into a 409. A new sender of the two commands has to be reviewed against
/// the lock and then added here. The REST controller dispatches them generically (InputBaseController), so it does not
/// appear in the scan.
/// </summary>

using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class ShiftHandlerSendersGuardTests
{
    private const string SourceFilePattern = "*.cs";
    private const string LineCommentPrefix = "//";
    private const int MinimumScannedFiles = 500;

    private static readonly string[] SkippedDirectories = ["bin", "obj", "Migrations"];

    private static readonly string[] CommandConstructions =
    [
        "new PutCommand<ShiftResource>(",
        "new DeleteCommand<ShiftResource>("
    ];

    private static readonly IReadOnlyDictionary<string, string> AllowedSenders = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["Application/Skills/UpdateShiftSkill.cs"] = "update_shift: the lock must apply to what the model changes",
        ["Application/Skills/DeleteShiftSkill.cs"] = "delete_shift: the lock must apply to what the model deletes"
    };

    [Test]
    public void OnlyTheKnownSkills_DispatchThePutAndDeleteShiftCommands()
    {
        var apiRoot = RepositoryRootLocator.ApiProject;
        var scanned = 0;
        var senders = new SortedSet<string>(StringComparer.Ordinal);

        foreach (var file in Directory.EnumerateFiles(apiRoot, SourceFilePattern, SearchOption.AllDirectories))
        {
            var relative = Path.GetRelativePath(apiRoot, file).Replace(Path.DirectorySeparatorChar, '/');
            if (relative.Split('/').SkipLast(1).Any(segment => SkippedDirectories.Contains(segment, StringComparer.Ordinal)))
            {
                continue;
            }

            scanned++;
            var sends = File.ReadLines(file)
                .Where(line => !line.TrimStart().StartsWith(LineCommentPrefix, StringComparison.Ordinal))
                .Any(line => CommandConstructions.Any(construction => line.Contains(construction, StringComparison.Ordinal)));
            if (sends)
            {
                senders.Add(relative);
            }
        }

        scanned.ShouldBeGreaterThan(MinimumScannedFiles, "The scan did not read the real Klacks.Api sources.");
        senders.ToList().ShouldBe(
            AllowedSenders.Keys.OrderBy(path => path, StringComparer.Ordinal).ToList(),
            customMessage: "A new sender of PutCommand<ShiftResource> or DeleteCommand<ShiftResource> runs into the shift lock. Review it "
            + "against LockedShiftEditPolicy and add it to AllowedSenders, or write through the repository like the cut editor.");
    }
}
