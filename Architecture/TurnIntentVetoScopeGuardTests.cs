// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Text.RegularExpressions;

namespace Klacks.UnitTest.Architecture;

/// <summary>
/// The turn-intent veto may lift only the mutation detector's reason to force a tool call. The no-tool nudge and the
/// completion-claim guards must keep reading the raw detector signal, otherwise a wrongly vetoed "create a contract
/// for X" could end in a claimed success without any tool call. Source scan of the two chat loops (StreamedChatTurn,
/// MultiTurnChatLoop): the veto flag is read only inside each loop's ToolChoicePolicy.ResolveToolChoice call, and the
/// non-streaming loop drops it once the forced retry after a tool-less completion claim fired, so that retry stays forced. A second scan makes sure every LLMContext built from an
/// assembled toolset copies the veto, so a new entry point cannot switch it off silently.
/// </summary>
[TestFixture]
public class TurnIntentVetoScopeGuardTests
{
    private const string StreamedLoopRelativePath = "Klacks.Api/Domain/Services/Assistant/ChatTurn/StreamedChatTurn.cs";
    private const string NonStreamingLoopRelativePath = "Klacks.Api/Domain/Services/Assistant/ChatTurn/MultiTurnChatLoop.cs";
    private const string ChatServiceRelativePath = "Klacks.Api/Domain/Services/Assistant/LLMService.cs";
    private const string ChatTurnDirectory = "Klacks.Api/Domain/Services/Assistant/ChatTurn";
    private const string VetoFlag = "TurnIntentVetoesForcedToolCall";
    private const string VetoUntilForcedRetry = "TurnIntentVetoesForcedToolCall && !state.ForcedRetryUsed";
    private const string ApplicationDirectory = "Klacks.Api/Application";
    private const string ToolsetContextMarker = "HasDomainSkillContext = toolset.HasDomainSkillContext";
    private const string ToolsetVetoAssignment = "TurnIntentVetoesForcedToolCall = toolset.TurnIntentVetoesForcedToolCall";

    [TestCase(StreamedLoopRelativePath)]
    [TestCase(NonStreamingLoopRelativePath)]
    public void ChatLoop_ReadsTheVetoOnlyForTheToolChoice(string loopRelativePath)
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), loopRelativePath));

        Regex.Matches(source, VetoFlag).Count.ShouldBe(1);
        Regex.Matches(source, @"ToolChoicePolicy\.ResolveToolChoice\([^;]*?\.TurnIntentVetoesForcedToolCall")
            .Count.ShouldBe(1);
    }

    [Test]
    public void ChatServiceAndOtherChatTurnParts_NeverReadTheVeto()
    {
        var root = FindRepositoryRoot();
        var loops = new[] { StreamedLoopRelativePath, NonStreamingLoopRelativePath }
            .Select(path => Path.GetFullPath(Path.Combine(root, path)))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);
        var others = Directory.EnumerateFiles(Path.Combine(root, ChatTurnDirectory), "*.cs")
            .Select(Path.GetFullPath)
            .Where(file => !loops.Contains(file))
            .Append(Path.GetFullPath(Path.Combine(root, ChatServiceRelativePath)))
            .ToList();

        others.ShouldNotBeEmpty();
        others.ShouldAllBe(file => !File.ReadAllText(file).Contains(VetoFlag));
    }

    [Test]
    public void NonStreamingLoop_DropsTheVetoForTheForcedRetry_AndNudgesOnTheRawDetectorSignal()
    {
        var source = File.ReadAllText(Path.Combine(FindRepositoryRoot(), NonStreamingLoopRelativePath));

        source.ShouldContain(VetoUntilForcedRetry);
        Regex.Matches(source, @"ForceToolNudgePolicy\.ShouldForceToolNudge\(\s*state\.IsMutationIntent,").Count.ShouldBeGreaterThan(0);
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
        while (directory != null && !File.Exists(Path.Combine(directory.FullName, StreamedLoopRelativePath)))
        {
            directory = directory.Parent;
        }

        directory.ShouldNotBeNull($"{StreamedLoopRelativePath} not found above the test output");
        return directory.FullName;
    }
}
