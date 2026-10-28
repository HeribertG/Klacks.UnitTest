// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the deployment switches of the Playground hardening: the Playground stack
/// (deploy/docker-compose-server.yml) switches off auto-memory and the learning background services through
/// configuration, and the customer on-prem stack carries none of these switches, so customer installations keep the
/// defaults.
/// </summary>

using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundComposeSwitchesTests
{
    private const string YamlCommentMarker = "#";

    private static string ServerCompose() =>
        WithoutComments(File.ReadAllText(Path.Combine(RepositoryRootLocator.ApiProject, "deploy", "docker-compose-server.yml")));

    private static string OnPremCompose() =>
        WithoutComments(File.ReadAllText(Path.Combine(RepositoryRootLocator.ApiProject, "deploy", "onprem", "docker-compose.yml")));

    private static string WithoutComments(string yaml) =>
        string.Join('\n', yaml
            .Split('\n')
            .Select(line => line.Split(YamlCommentMarker, 2)[0])
            .Where(line => !string.IsNullOrWhiteSpace(line)));

    [Test]
    public void CommentedOutLines_AreNotCountedAsSwitches()
    {
        WithoutComments("- Keep=1\n  # - Assistant__AutoMemory__Enabled=false\n- Other=2 # Playground__Note").ShouldBe("- Keep=1\n- Other=2 ");
    }

    [TestCase("Assistant__AutoMemory__Enabled=false")]
    [TestCase("BackgroundServices__KlacksyLearning=false")]
    [TestCase("BackgroundServices__SkillRelationLearning=false")]
    [TestCase("BackgroundServices__GoalReflection=false")]
    [TestCase("BackgroundServices__GoalReflectionDelivery=false")]
    [TestCase("BackgroundServices__GoalReflectionPlanDrafting=false")]
    [TestCase("BackgroundServices__GoalReflectionExecution=false")]
    public void PlaygroundStack_SwitchesTheLearningWriterOff(string setting)
    {
        ServerCompose().ShouldContain($"- {setting}");
    }

    [TestCase("Assistant__AutoMemory")]
    [TestCase("BackgroundServices__KlacksyLearning")]
    [TestCase("BackgroundServices__SkillRelationLearning")]
    [TestCase("BackgroundServices__GoalReflection")]
    [TestCase("Playground__")]
    public void OnPremStack_CarriesNoPlaygroundSwitch(string setting)
    {
        OnPremCompose().ShouldNotContain(setting);
    }
}
