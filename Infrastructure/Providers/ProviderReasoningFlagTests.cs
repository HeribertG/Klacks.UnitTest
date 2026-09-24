// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guard test for the LLM provider adapters, in the spirit of HubAuthorizationTests: every provider
/// that consumes the reasoning_content channel MUST resolve it through ReasoningContentResolver, report
/// LLMProviderResponse.ReasoningWithoutContent, and never yield the buffered reasoning as a stream token.
/// Until 2026-09-24 reasoning was returned as the answer whenever content was empty, and users saw the
/// model's deliberation (about a tool it did not have) as Klacksy's reply. A provider that builds its
/// answer from the channel by hand, or flushes its reasoning buffer into the stream, reopens that leak.
///
/// The check reads the sources with File.ReadAllText deliberately: three provider files in this
/// repository are classified as binary by grep and are skipped by it without any message, so a text
/// search over the working tree is not a trustworthy way to verify this rule.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.UnitTest.Autofill.Support;

namespace Klacks.UnitTest.Infrastructure.Providers;

[TestFixture]
public class ProviderReasoningFlagTests
{
    private const string ApiProjectFolderName = "Klacks.Api";
    private const string ProvidersRelativePath = "Infrastructure/Services/Assistant/Providers";
    private const string ReasoningChannelMarker = "ReasoningContent";
    private const string ResponseConstructionMarker = "new LLMProviderResponse";
    private const string RequiredResolverMarker = "ReasoningContentResolver.Resolve(";
    private const string RequiredFlagMarker = "ReasoningWithoutContent";

    private static readonly Regex ReasoningYieldPattern = new(
        @"yield\s+return\s+[^;]*reasoning[^;]*;", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static IEnumerable<string> ProviderSourceFiles()
    {
        var providersRoot = ProvidersRoot();
        if (!Directory.Exists(providersRoot))
        {
            return [$"MISSING:{providersRoot}"];
        }

        var matching = Directory
            .EnumerateFiles(providersRoot, "*.cs", SearchOption.AllDirectories)
            .Where(ConsumesReasoningChannelAndBuildsAResponse)
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToList();

        // An empty source list would make every case vanish and the fixture pass green, which is the
        // one failure mode this guard must not have.
        return matching.Count > 0 ? matching : ["NONE_FOUND"];
    }

    [TestCaseSource(nameof(ProviderSourceFiles))]
    public void Provider_ConsumingReasoningChannel_ResolvesItAndReportsReasoningWithoutContent(string sourceFile)
    {
        var source = ReadGuardedSource(sourceFile);

        source.ShouldContain(
            RequiredResolverMarker,
            Case.Sensitive,
            $"{Path.GetFileName(sourceFile)} reads the reasoning channel without {RequiredResolverMarker}...). "
            + "Resolve the answer there: it never turns reasoning into content.");
        source.ShouldContain(
            RequiredFlagMarker,
            Case.Sensitive,
            $"{Path.GetFileName(sourceFile)} never reports {RequiredFlagMarker} on its {ResponseConstructionMarker}. "
            + "Callers such as the opening greeting use the flag to recognise a reasoning-only reply.");
    }

    [TestCaseSource(nameof(ProviderSourceFiles))]
    public void Provider_ConsumingReasoningChannel_NeverYieldsReasoningAsAStreamToken(string sourceFile)
    {
        var source = ReadGuardedSource(sourceFile);

        var leaks = ReasoningYieldPattern.Matches(source).Select(match => match.Value).ToList();

        leaks.ShouldBeEmpty(
            $"{Path.GetFileName(sourceFile)} yields reasoning into the answer stream: {string.Join(" | ", leaks)}. "
            + "Buffer reasoning for logging only (ReasoningChannelLog); it is never the answer.");
    }

    private static string ReadGuardedSource(string sourceFile)
    {
        sourceFile.StartsWith("MISSING:", StringComparison.Ordinal).ShouldBeFalse(
            $"Provider sources were not found ({sourceFile}) - "
            + "fix the path in this test, do not delete the guard.");
        sourceFile.ShouldNotBe(
            "NONE_FOUND",
            $"No provider builds an {ResponseConstructionMarker} from the {ReasoningChannelMarker} channel any more. "
            + "If that is a deliberate change, remove this guard explicitly; a silently empty guard protects nothing.");

        return File.ReadAllText(sourceFile);
    }

    private static bool ConsumesReasoningChannelAndBuildsAResponse(string path)
    {
        var source = File.ReadAllText(path);
        return source.Contains(ReasoningChannelMarker, StringComparison.Ordinal)
            && source.Contains(ResponseConstructionMarker, StringComparison.Ordinal);
    }

    private static string ProvidersRoot()
    {
        var repositoryRoot = Directory.GetParent(AutofillRepositoryPaths.TestProjectRoot)?.FullName
            ?? AutofillRepositoryPaths.TestProjectRoot;
        return Path.Combine(repositoryRoot, ApiProjectFolderName, ProvidersRelativePath);
    }
}
