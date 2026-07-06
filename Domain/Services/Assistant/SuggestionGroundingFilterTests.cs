// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SuggestionGroundingFilter — verifies it drops suggestion-chip candidates that do
/// not match a real entity name and keeps exact and partial (case-insensitive) matches, mirroring
/// the resolution rule used by the recipe skills (AssignContractByNameSkill / GroupResolver) so a
/// hallucinated chip like "Standardvertrag" is caught before it ever reaches the user.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class SuggestionGroundingFilterTests
{
    [Test]
    public void Filter_DropsCandidateNotMatchingAnyRealName()
    {
        var result = SuggestionGroundingFilter.Filter(
            ["Standardvertrag"], ["Vollzeit 160 BE", "Teilzeit 80 BE"]);

        result.ShouldBeEmpty();
    }

    [Test]
    public void Filter_KeepsExactCaseInsensitiveMatch()
    {
        var result = SuggestionGroundingFilter.Filter(
            ["vollzeit 160 be"], ["Vollzeit 160 BE"]);

        result.ShouldBe(["vollzeit 160 be"]);
    }

    [Test]
    public void Filter_KeepsPartialSubstringMatch()
    {
        var result = SuggestionGroundingFilter.Filter(
            ["Bern"], ["Bern-wöchentlich"]);

        result.ShouldBe(["Bern"]);
    }

    [Test]
    public void Filter_MixedCandidates_KeepsOnlyGroundedOnes()
    {
        var result = SuggestionGroundingFilter.Filter(
            ["Service-Team", "Bern"], ["Bern", "Zürich"]);

        result.ShouldBe(["Bern"]);
    }

    [Test]
    public void Filter_EmptyCandidateList_ReturnsEmpty()
    {
        var result = SuggestionGroundingFilter.Filter([], ["Bern"]);

        result.ShouldBeEmpty();
    }

    [Test]
    public void Filter_BlankCandidate_IsDropped()
    {
        var result = SuggestionGroundingFilter.Filter(["   "], ["Bern"]);

        result.ShouldBeEmpty();
    }
}
