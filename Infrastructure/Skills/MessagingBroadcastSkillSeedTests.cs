// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the seeds of send_broadcast and evaluate_messaging_reach and everything that has to agree with them outside the plugin
/// seed file: effect and category, the gate that equals MessagingController, the area, the advisory edge in the core relation
/// seeds, the risk class of the real classifier, the curated skill lists, the Playground block and the plugin manifest.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.Skills.Meta;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Infrastructure.Skills;

[TestFixture]
public class MessagingBroadcastSkillSeedTests
{
    private const string SendBroadcast = "send_broadcast";
    private const string EvaluateReach = "evaluate_messaging_reach";
    private const int AdvisesForType = 3;
    private const string MessagingArea = "messaging";

    private static JsonElement PluginSeed(string name)
    {
        var path = RepositoryRootLocator.RequireFile("Klacks.Api", "Plugins", "Features", MessagingArea, "skill-seeds.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));
        return document.RootElement.EnumerateArray().Single(skill => skill.GetProperty("name").GetString() == name).Clone();
    }

    [Test]
    public void TheBroadcast_IsAMutatingSkillBehindTheControllersRoles()
    {
        var seed = PluginSeed(SendBroadcast);

        seed.GetProperty("effect").GetString().ShouldBe("Mutate");
        seed.GetProperty("alwaysOn").GetBoolean().ShouldBeFalse();
        seed.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()).ShouldBe(new[] { Permissions.CanEditClients });
    }

    [Test]
    public void TheReachAdvice_IsAnAdviseQuery()
    {
        var seed = PluginSeed(EvaluateReach);

        seed.GetProperty("effect").GetString().ShouldBe("Advise");
        seed.GetProperty("category").GetString().ShouldBe("Query");
        seed.GetProperty("requiredPermissions").EnumerateArray().Select(p => p.GetString()).ShouldBe(new[] { Permissions.CanEditClients });
    }

    [TestCase(SendBroadcast)]
    [TestCase(EvaluateReach)]
    public void BothSkills_CarryTheMessagingArea(string name) =>
        PluginSeed(name).GetProperty("area").GetString().ShouldBe(MessagingArea);

    [Test]
    public void TheCoreRelationSeeds_HoldTheAdvisoryEdge()
    {
        var path = RepositoryRootLocator.RequireFile("Klacks.Api", "Application", "Skills", "Definitions", "skill-relation-seeds.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        document.RootElement.GetProperty("relations").EnumerateArray().Count(relation =>
            relation.GetProperty("skillAName").GetString() == EvaluateReach
            && relation.GetProperty("skillBName").GetString() == SendBroadcast
            && relation.GetProperty("type").GetInt32() == AdvisesForType).ShouldBe(1);
    }

    [Test]
    public void TheRealClassifier_HoldsTheBroadcast_AndReadsTheAdvice()
    {
        var classifier = new SkillRiskClassifier();

        classifier.Classify(new SkillDescriptor(SendBroadcast, "guard", SkillCategory.Action, [], [], [], null))
            .ShouldBe(SkillRiskClass.Sensitive);
        classifier.Classify(new SkillDescriptor(EvaluateReach, "guard", SkillCategory.Query, [], [], [], null))
            .ShouldBe(SkillRiskClass.ReadOnly);
    }

    [TestCase(SendBroadcast)]
    [TestCase(EvaluateReach)]
    public void BothSkills_AreBoundToAVisibleUser_AndClosedOnCappedChannels(string name)
    {
        GroupVisibilityBoundSkills.Contains(name).ShouldBeTrue();
        CappedChannelSkillExclusions.Contains(name).ShouldBeTrue();
    }

    [Test]
    public void TheBroadcast_IsBlockedOnThePlayground() =>
        PlaygroundBlockedSkills.Names.ShouldContain(SendBroadcast);

    [Test]
    public void TheManifest_NamesBothSkills_AndWhatsAppAsTwoWay()
    {
        var path = RepositoryRootLocator.RequireFile("Klacks.Api", "Plugins", "Features", MessagingArea, "manifest.json");
        using var document = JsonDocument.Parse(File.ReadAllText(path));

        var skills = document.RootElement.GetProperty("providedSkills").EnumerateArray().Select(skill => skill.GetString()).ToList();
        skills.ShouldContain(SendBroadcast);
        skills.ShouldContain(EvaluateReach);
        document.RootElement.GetProperty("description").GetString()!
            .ShouldStartWith("Send and receive messages via Telegram, WhatsApp,");
    }
}
