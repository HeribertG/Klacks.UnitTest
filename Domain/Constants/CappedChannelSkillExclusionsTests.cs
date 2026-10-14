// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the skills B8 opened to the Supervisor role and that a capped channel (Slack owner bridge, MCP) must therefore
/// withhold, the four skills the Supervisor already reached before B8 (which stay available), the matching rules of Blocks
/// and that every excluded name is a real seeded skill (a typo would silently exclude nothing).
/// </summary>

using System.Text.Json.Nodes;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Domain.Constants;

[TestFixture]
public class CappedChannelSkillExclusionsTests
{
    private static readonly string[] OpenedByB8 =
    [
        "send_message", "read_messages", "list_email_folders", "mark_email_read", "move_email_to_folder", "delete_email",
        "restore_email", "fetch_new_emails", "translate_email", "list_emails_by_group", "get_email_group_tree"
    ];

    private static readonly string[] ReachableBeforeB8 =
    [
        "list_emails", "read_email", "get_email_analysis", "list_emails_by_client"
    ];

    private static readonly string[][] SeedRelativePaths =
    [
        ["Klacks.Api", "Application", "Skills", "Definitions", "skill-seeds.json"],
        ["Klacks.Api", "Plugins", "Features", "messaging", "skill-seeds.json"]
    ];

    [Test]
    public void TheExclusions_AreExactlyTheSkillsB8OpenedToTheSupervisor()
    {
        CappedChannelSkillExclusions.All.OrderBy(name => name, StringComparer.Ordinal)
            .ShouldBe(OpenedByB8.OrderBy(name => name, StringComparer.Ordinal));
    }

    [TestCaseSource(nameof(ReachableBeforeB8))]
    public void ASkillTheSupervisorReachedBeforeB8_IsNotExcluded(string skill)
    {
        CappedChannelSkillExclusions.Contains(skill).ShouldBeFalse();
        CappedChannelSkillExclusions.Blocks(skill, CappedChannels.SlackOwnerBridge, PersonalAccessTokenAccessMode.Write)
            .ShouldBeFalse();
    }

    [Test]
    public void EveryExcludedName_IsASeededSkill()
    {
        var seeded = new HashSet<string>(StringComparer.Ordinal);
        foreach (var relativePath in SeedRelativePaths)
        {
            var root = JsonNode.Parse(File.ReadAllText(RepositoryRootLocator.RequireFile(relativePath)))!;
            var skills = root is JsonArray pluginSeeds ? pluginSeeds : root["skills"]!.AsArray();
            foreach (var skill in skills)
            {
                seeded.Add(skill!["name"]!.GetValue<string>());
            }
        }

        CappedChannelSkillExclusions.All.Where(name => !seeded.Contains(name)).ShouldBeEmpty();
    }

    [TestCaseSource(nameof(OpenedByB8))]
    public void AnExcludedSkill_IsBlockedOnTheSlackBridge_AndOverMcp_ButNotInTheChat(string skill)
    {
        CappedChannelSkillExclusions.Blocks(skill, CappedChannels.SlackOwnerBridge, null).ShouldBeTrue();
        CappedChannelSkillExclusions.Blocks(skill, null, PersonalAccessTokenAccessMode.Read).ShouldBeTrue();
        CappedChannelSkillExclusions.Blocks(skill, null, null).ShouldBeFalse();
        CappedChannelSkillExclusions.Blocks(skill, string.Empty, null).ShouldBeFalse();
    }

    [Test]
    public void ASkillOutsideTheList_IsNeverBlocked_AndTheNameMatchIgnoresCase()
    {
        CappedChannelSkillExclusions.Blocks("update_client", CappedChannels.SlackOwnerBridge, null).ShouldBeFalse();
        CappedChannelSkillExclusions.Blocks(null, CappedChannels.SlackOwnerBridge, null).ShouldBeFalse();
        CappedChannelSkillExclusions.Blocks("READ_MESSAGES", CappedChannels.SlackOwnerBridge, null).ShouldBeTrue();
    }
}
