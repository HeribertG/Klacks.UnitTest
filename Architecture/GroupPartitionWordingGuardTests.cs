// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class GroupPartitionWordingGuardTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string ForbiddenWord = "canton";

    private static readonly string[] GuardedFiles =
    [
        "Domain/Enums/GroupPartitionLevelEnum.cs",
        "Domain/Services/Geo/AddressClusterPlanner.cs",
        "Application/Services/Grouping/GroupPartitionPlanner.cs",
        "Application/DTOs/Grouping/GroupPartitionContext.cs",
        "Application/DTOs/Grouping/PlannedPartitionGroup.cs",
        "Application/DTOs/Grouping/PartitionClientAssignment.cs",
        "Application/DTOs/Grouping/UnassignablePartitionClient.cs",
        "Application/DTOs/Grouping/GroupPartitionPlan.cs",
        "Application/Commands/Groups/PartitionClientsByAddressCommand.cs",
        "Application/DTOs/Groups/PartitionClientsByAddressResult.cs",
        "Application/DTOs/Groups/PartitionGroupSummary.cs",
        "Application/Handlers/Groups/PartitionClientsByAddressCommandHandler.cs",
        "Application/Skills/PartitionClientsByAddressSkill.cs",
        "Application/Services/Orders/OrderGroupPlanner.cs"
    ];

    [Test]
    public void GroupingCode_DoesNotHardCodeTheSwissTermCanton()
    {
        var root = Path.Combine(FindRepositoryRoot(), ApiProjectDirectory);
        var violations = new List<string>();

        foreach (var relative in GuardedFiles)
        {
            var path = Path.Combine(root, relative);
            File.Exists(path).ShouldBeTrue($"guarded file missing: {relative}");
            var lines = File.ReadAllLines(path);
            for (var i = 0; i < lines.Length; i++)
            {
                if (lines[i].Contains(ForbiddenWord, StringComparison.OrdinalIgnoreCase))
                {
                    violations.Add($"{relative}:{i + 1}: {lines[i].Trim()}");
                }
            }
        }

        violations.ShouldBeEmpty(string.Join(Environment.NewLine, violations));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !Directory.Exists(Path.Combine(directory.FullName, ApiProjectDirectory)))
        {
            directory = directory.Parent;
        }

        return directory?.FullName ?? throw new DirectoryNotFoundException("Repository root with Klacks.Api not found.");
    }
}
