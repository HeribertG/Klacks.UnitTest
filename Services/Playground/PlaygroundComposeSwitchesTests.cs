// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the deployment switches of the Playground hardening: the Playground stack
/// (deploy/docker-compose-server.yml) switches off auto-memory and the learning background services through
/// configuration, and the customer on-prem stack carries none of these switches, so customer installations keep the
/// defaults. The Playground mode itself is off in that compose file unless the host's .env switches it on, and both
/// workflows that deploy the Playground host write that switch only when the host has not set it.
/// </summary>

using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundComposeSwitchesTests
{
    private const string YamlCommentMarker = "#";
    private const string PlaygroundSwitchOffByDefault = "- Deployment__IsPlayground=${DEPLOYMENT_IS_PLAYGROUND:-false}";
    private const string PlaygroundSwitchWrittenOnHost =
        "grep -q '^DEPLOYMENT_IS_PLAYGROUND=' .env 2>/dev/null || { [ -s .env ] && [ -n \"$(tail -c1 .env)\" ] && echo >> .env; echo \"DEPLOYMENT_IS_PLAYGROUND=true\" >> .env; }";
    private const string GitHubFolder = ".github";
    private const string WorkflowsFolder = "workflows";

    private static string Workflow(string fileName) =>
        File.ReadAllText(Path.Combine(RepositoryRootLocator.ApiProject, GitHubFolder, WorkflowsFolder, fileName));

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

    [Test]
    public void PlaygroundStack_IsNoPlaygroundUnlessTheHostSaysSo()
    {
        ServerCompose().ShouldContain(PlaygroundSwitchOffByDefault);
    }

    [TestCase("deploy.yml")]
    [TestCase("reset-and-deploy.yml")]
    public void PlaygroundHostWorkflow_WritesThePlaygroundSwitch(string workflow)
    {
        WithoutComments(Workflow(workflow)).ShouldContain(PlaygroundSwitchWrittenOnHost);
    }

    [TestCase("Assistant__AutoMemory")]
    [TestCase("BackgroundServices__KlacksyLearning")]
    [TestCase("BackgroundServices__SkillRelationLearning")]
    [TestCase("BackgroundServices__GoalReflection")]
    [TestCase("Playground__")]
    [TestCase("IsPlayground")]
    [TestCase("DEPLOYMENT_IS_PLAYGROUND")]
    public void OnPremStack_CarriesNoPlaygroundSwitch(string setting)
    {
        OnPremCompose().ShouldNotContain(setting);
    }
}
