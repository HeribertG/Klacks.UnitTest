// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Keeps the topic keys the UI sends to assistant/explain-offers (EXPLAIN_OFFER_TOPIC in
/// Klacks.Ui/src/app/domain/constants/explain-offer.constants.ts) in step with UiExplainOfferTopics. The endpoint answers
/// an unknown topic with 400 and the UI swallows that failure quietly, so a renamed key would silently stop the offer or
/// the click count. Inconclusive when Klacks.Ui is not checked out next to Klacks.Api.
/// </summary>

using System.Text.RegularExpressions;
using Klacks.Api.Domain.Constants;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.Architecture;

[TestFixture]
public class UiExplainOfferTopicKeysUiMirrorGuardTests
{
    private const string UiConstantsRelativePath = "Klacks.Ui/src/app/domain/constants/explain-offer.constants.ts";
    private const string TopicBlockPattern = @"EXPLAIN_OFFER_TOPIC\s*=\s*\{(?<body>[^}]*)\}";
    private const string TopicValuePattern = @":\s*'(?<key>[^']+)'";

    [Test]
    public void TheUiTopicKeys_AreExactlyTheBackendTopics()
    {
        var path = RepositoryRootLocator.FindFile(UiConstantsRelativePath);
        if (path == null)
        {
            Assert.Inconclusive(RepositoryRootLocator.NotFoundMessage(UiConstantsRelativePath));
            return;
        }

        var block = Regex.Match(File.ReadAllText(path), TopicBlockPattern, RegexOptions.CultureInvariant);
        block.Success.ShouldBeTrue("EXPLAIN_OFFER_TOPIC not found");
        var uiKeys = Regex.Matches(block.Groups["body"].Value, TopicValuePattern, RegexOptions.CultureInvariant)
            .Select(match => match.Groups["key"].Value)
            .OrderBy(key => key, StringComparer.Ordinal)
            .ToList();

        uiKeys.ShouldBe(UiExplainOfferTopics.All.Select(topic => topic.Key).OrderBy(key => key, StringComparer.Ordinal).ToList());
    }
}
