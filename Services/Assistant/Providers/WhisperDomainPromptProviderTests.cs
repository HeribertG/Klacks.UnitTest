// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Services.Assistant.Providers;

using Klacks.Api.Infrastructure.Services.Assistant.Providers.Stt;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class WhisperDomainPromptProviderTests
{
    [Test]
    public void BuildPrompt_ShouldReturnBaseGlossary_WhenNoAdditionalTerms()
    {
        var result = WhisperDomainPromptProvider.BuildPrompt("de", []);

        result.ShouldBe(WhisperDomainPromptProvider.GetPrompt("de"));
    }

    [Test]
    public void BuildPrompt_ShouldAppendAdditionalTerms()
    {
        var result = WhisperDomainPromptProvider.BuildPrompt("de", ["Spitex Aarau", "Frühdienst"]);

        result.ShouldStartWith(WhisperDomainPromptProvider.GetPrompt("de"));
        result.ShouldContain("Spitex Aarau, Frühdienst.");
    }

    [Test]
    public void BuildPrompt_ShouldSkipTermsAlreadyInBaseGlossary()
    {
        var result = WhisperDomainPromptProvider.BuildPrompt("de", ["Klacksy", "Dashboard"]);

        result.ShouldBe(WhisperDomainPromptProvider.GetPrompt("de"));
    }

    [Test]
    public void BuildPrompt_ShouldDeduplicateAdditionalTerms_IgnoringCase()
    {
        var result = WhisperDomainPromptProvider.BuildPrompt("de", ["Spitex", "spitex", "SPITEX"]);

        result.ShouldContain("Spitex");
        result.ShouldNotContain("spitex,");
    }

    [Test]
    public void BuildPrompt_ShouldStayWithinPromptBudget_WhenManyLongTermsSupplied()
    {
        var terms = Enumerable.Range(1, 100)
            .Select(i => $"Sehr-langer-Fachbegriff-Nummer-{i:D3}-für-die-Dienstplanung")
            .ToList();

        var result = WhisperDomainPromptProvider.BuildPrompt("de", terms);

        result.Length.ShouldBeLessThanOrEqualTo(700);
        result.ShouldStartWith(WhisperDomainPromptProvider.GetPrompt("de"));
    }
}
