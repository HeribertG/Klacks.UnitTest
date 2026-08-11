// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Guard test for the LLM provider adapters, in the spirit of HubAuthorizationTests: every provider
/// that consumes the reasoning_content channel MUST also report it through
/// LLMProviderResponse.ContentFromReasoning. Callers that may never show raw chain-of-thought — the
/// opening greeting — reject an answer on that flag alone, so a provider that resolves the channel
/// without setting the flag silently reopens the leak for its models.
///
/// The check reads the sources with File.ReadAllText deliberately: three provider files in this
/// repository are classified as binary by grep and are skipped by it without any message, so a text
/// search over the working tree is not a trustworthy way to verify this rule.
/// </summary>

using Klacks.UnitTest.Autofill.Support;

namespace Klacks.UnitTest.Infrastructure.Providers;

[TestFixture]
public class ProviderReasoningFlagTests
{
    private const string ApiProjectFolderName = "Klacks.Api";
    private const string ProvidersRelativePath = "Infrastructure/Services/Assistant/Providers";
    private const string ReasoningChannelMarker = "ReasoningContent";
    private const string ResponseConstructionMarker = "new LLMProviderResponse";
    private const string RequiredFlagMarker = "ContentFromReasoning";

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
    public void Provider_ConsumingReasoningChannel_MustReportContentFromReasoning(string sourceFile)
    {
        sourceFile.StartsWith("MISSING:", StringComparison.Ordinal).ShouldBeFalse(
            $"Provider sources were not found ({sourceFile}) — "
            + "fix the path in this test, do not delete the guard.");
        sourceFile.ShouldNotBe(
            "NONE_FOUND",
            $"No provider builds an {ResponseConstructionMarker} from the {ReasoningChannelMarker} channel any more. "
            + "If that is a deliberate change, remove this guard explicitly; a silently empty guard protects nothing.");

        var source = File.ReadAllText(sourceFile);

        source.ShouldContain(
            RequiredFlagMarker,
            Case.Sensitive,
            $"{Path.GetFileName(sourceFile)} resolves the reasoning channel but never sets "
            + $"{RequiredFlagMarker} on its {ResponseConstructionMarker}. Use "
            + "ReasoningContentResolver.Resolve(...) and pass FromReasoning through, otherwise a reasoning "
            + "model behind this provider can leak its chain-of-thought into the opening greeting.");
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
