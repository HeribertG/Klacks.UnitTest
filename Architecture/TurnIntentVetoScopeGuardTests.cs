// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Text.RegularExpressions;

namespace Klacks.UnitTest.Architecture;

/// <summary>
/// The turn-intent veto may lift only the mutation detector's reason to force a tool call. The no-tool nudge and the
/// completion-claim guards must keep reading the raw detector signal, otherwise a wrongly vetoed "create a contract
/// for X" could end in a claimed success without any tool call. Source scan of LLMService: the veto flag is read
/// only inside the two ToolChoicePolicy.ResolveToolChoice calls, and the non-streaming loop drops it once the forced
/// retry after a tool-less completion claim fired, so that retry stays forced. A second scan makes sure every LLMContext built from an
/// assembled toolset copies the veto, so a new entry point cannot switch it off silently.
/// </summary>
[TestFixture]
public class TurnIntentVetoScopeGuardTests
{
    private const string LlmServiceRelativePath = "Klacks.Api/Domain/Services/Assistant/LLMService.cs";
    private const string VetoFlag = "TurnIntentVetoesForcedToolCall";
    private const int ChatLoops = 2;
    private const string VetoUntilForcedRetry = "TurnIntentVetoesForcedToolCall && !forcedRetryUsed";
    private const string ApplicationDirectory = "Klacks.Api/Application";
    private const string ToolsetContextMarker = "HasDomainSkillContext = toolset.HasDomainSkillContext";
    private const string ToolsetVetoAssignment = "TurnIntentVetoesForcedToolCall = toolset.TurnIntentVetoesForcedToolCall";

    [Test]
    public void LlmService_ReadsTheVetoOnlyForTheToolChoice()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), LlmServiceRelativePath));

        Regex.Matches(source, VetoFlag).Count.ShouldBe(ChatLoops);
        Regex.Matches(source, @"ToolChoicePolicy\.ResolveToolChoice\([^;]*?\.TurnIntentVetoesForcedToolCall")
            .Count.ShouldBe(ChatLoops);
        source.ShouldContain(VetoUntilForcedRetry);
        Regex.Matches(source, @"ForceToolNudgePolicy\.ShouldForceToolNudge\(\s*isMutationIntent,").Count.ShouldBeGreaterThan(0);
    }

    [Test]
    public void EveryContextBuiltFromAToolset_CarriesTheVeto()
    {
        var root = FindRepositoryRoot();
        var builders = Directory.EnumerateFiles(Path.Combine(root, ApplicationDirectory), "*.cs", SearchOption.AllDirectories)
            .Where(file => File.ReadAllText(file).Contains(ToolsetContextMarker))
            .ToList();

        builders.ShouldNotBeEmpty();
        builders.ShouldAllBe(file => File.ReadAllText(file).Contains(ToolsetVetoAssignment));
    }

    private static string FindRepositoryRoot()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, LlmServiceRelativePath)))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull($"{LlmServiceRelativePath} not found above the test output");
        return directory.FullName;
    }
}
