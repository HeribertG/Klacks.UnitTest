// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the voice-mode suppression of the text-only affordance rules
/// (SUGGESTION_FORMAT follow-up chips and SUGGESTED_REPLIES_FORMAT interactive/date-picker chips)
/// in IdentityContextProvider: voice turns omit both rules (chips are unusable hands-free and
/// generating them delays the spoken turn) while keeping workflow-discipline rules, text turns
/// keep everything, and the two variants are cached under separate keys so neither mode leaks
/// into the other.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Microsoft.Extensions.Caching.Memory;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class IdentityContextProviderSuggestionRuleTests
{
    private const string SuggestionRuleContent = "append exactly 3 short follow-up suggestions";
    private const string RepliesRuleContent = "append a REPLIES block";
    private const string WorkflowRuleContent = "use a GUIDED step-by-step workflow";
    private const string OtherRuleContent = "Always fill in state and country fields.";

    private IAgentSoulRepository _soulRepository = null!;
    private IGlobalAgentRuleRepository _globalRuleRepository = null!;
    private ILanguageMetadataProvider _languageMetadata = null!;
    private IdentityContextProvider _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _soulRepository = Substitute.For<IAgentSoulRepository>();
        _soulRepository.GetActiveSectionsAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new List<AgentSoulSection>());

        _globalRuleRepository = Substitute.For<IGlobalAgentRuleRepository>();
        _globalRuleRepository.GetActiveRulesAsync(Arg.Any<CancellationToken>())
            .Returns(new List<GlobalAgentRule>
            {
                new() { Name = "ADDRESS_COMPLETENESS", Content = OtherRuleContent, SortOrder = 1 },
                new() { Name = GlobalAgentRuleNames.SuggestionFormat, Content = SuggestionRuleContent, SortOrder = 2 },
                new() { Name = GlobalAgentRuleNames.SuggestedRepliesFormat, Content = RepliesRuleContent, SortOrder = 3 },
                new() { Name = GlobalAgentRuleNames.GuidedWorkflow, Content = WorkflowRuleContent, SortOrder = 4 },
            });

        _languageMetadata = Substitute.For<ILanguageMetadataProvider>();
        _languageMetadata.GetName(Arg.Any<string>()).Returns("English");
        _languageMetadata.GetDisplayName(Arg.Any<string>()).Returns("English");

        _sut = new IdentityContextProvider(
            _soulRepository, _globalRuleRepository, _languageMetadata,
            new MemoryCache(new MemoryCacheOptions()));
    }

    [Test]
    public async Task VoiceMode_OmitsBothTextOnlyAffordanceRules_ButKeepsWorkflowAndOtherRules()
    {
        var prompt = await _sut.GetIdentityPromptAsync(Guid.NewGuid(), "en", suppressTextOnlyAffordances: true);

        prompt.ShouldNotContain(SuggestionRuleContent);
        prompt.ShouldNotContain(GlobalAgentRuleNames.SuggestionFormat);
        prompt.ShouldNotContain(RepliesRuleContent);
        prompt.ShouldNotContain(GlobalAgentRuleNames.SuggestedRepliesFormat);
        prompt.ShouldContain(WorkflowRuleContent);
        prompt.ShouldContain(OtherRuleContent);
    }

    [Test]
    public async Task TextMode_KeepsAllAffordanceRules()
    {
        var prompt = await _sut.GetIdentityPromptAsync(Guid.NewGuid(), "en", suppressTextOnlyAffordances: false);

        prompt.ShouldContain(SuggestionRuleContent);
        prompt.ShouldContain(RepliesRuleContent);
        prompt.ShouldContain(WorkflowRuleContent);
        prompt.ShouldContain(OtherRuleContent);
    }

    [Test]
    public async Task VoiceAndTextVariants_AreCachedSeparately()
    {
        var agentId = Guid.NewGuid();

        var voicePrompt = await _sut.GetIdentityPromptAsync(agentId, "en", suppressTextOnlyAffordances: true);
        var textPrompt = await _sut.GetIdentityPromptAsync(agentId, "en", suppressTextOnlyAffordances: false);

        voicePrompt.ShouldNotContain(SuggestionRuleContent);
        voicePrompt.ShouldNotContain(RepliesRuleContent);
        textPrompt.ShouldContain(SuggestionRuleContent);
        textPrompt.ShouldContain(RepliesRuleContent);
    }
}
