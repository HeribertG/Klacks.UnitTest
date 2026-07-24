// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ContextBudgetPolicy's anchor-table interpolation arithmetic (P2 of the Klacksy
/// memory redesign): exact reference-tier identity across the whole 100k-200k plateau (the
/// regression-critical case, since Anthropic's standard effective limit is exactly 200k), linear
/// interpolation at documented midpoints, and flat clamping below the lowest and above the highest
/// anchor.
/// </summary>

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;

namespace Klacks.UnitTest.Services.Assistant.Providers;

[TestFixture]
public class ContextBudgetPolicyTests
{
    private ContextBudgetPolicy _sut = null!;
    private ILLMProvider _provider = null!;
    private LLMModel _model = null!;

    [SetUp]
    public void SetUp()
    {
        _sut = new ContextBudgetPolicy();
        _provider = Substitute.For<ILLMProvider>();
        _model = new LLMModel();
    }

    private ContextBudgetProfile ResolveFor(int effectiveInputLimit)
    {
        _provider.GetEffectiveInputTokenLimit(Arg.Any<LLMModel>()).Returns(effectiveInputLimit);
        return _sut.Resolve(_provider, _model);
    }

    [TestCase(100_000)]
    [TestCase(150_000)]
    [TestCase(200_000)]
    public void ReferencePlateau_ReturnsExactlyTodaysFixedValues(int effectiveInputLimit)
    {
        var profile = ResolveFor(effectiveInputLimit);

        profile.MaxHistoryMessages.ShouldBe(20);
        profile.MaxToolsForProvider.ShouldBe(30);
        profile.MaxPinnedMemories.ShouldBe(10);
        profile.MaxMemoriesPerTurn.ShouldBe(5);
        profile.MaxToolResultChars.ShouldBe(8_000);
    }

    [Test]
    public void TinyAnchor_At16k_MatchesCalibratedFloor()
    {
        var profile = ResolveFor(16_000);

        profile.MaxHistoryMessages.ShouldBe(8);
        profile.MaxToolsForProvider.ShouldBe(12);
        profile.MaxPinnedMemories.ShouldBe(3);
        profile.MaxMemoriesPerTurn.ShouldBe(2);
        profile.MaxToolResultChars.ShouldBe(2_000);
    }

    [TestCase(8_000)]
    [TestCase(1_000)]
    [TestCase(0)]
    public void BelowTinyAnchor_ClampsFlatAtTinyProfile(int effectiveInputLimit)
    {
        var profile = ResolveFor(effectiveInputLimit);

        profile.MaxHistoryMessages.ShouldBe(8);
        profile.MaxToolsForProvider.ShouldBe(12);
        profile.MaxPinnedMemories.ShouldBe(3);
        profile.MaxMemoriesPerTurn.ShouldBe(2);
        profile.MaxToolResultChars.ShouldBe(2_000);
    }

    [Test]
    public void SmallAnchor_At32k_MatchesCalibratedValues()
    {
        var profile = ResolveFor(32_000);

        profile.MaxHistoryMessages.ShouldBe(12);
        profile.MaxToolsForProvider.ShouldBe(15);
        profile.MaxPinnedMemories.ShouldBe(5);
        profile.MaxMemoriesPerTurn.ShouldBe(3);
        profile.MaxToolResultChars.ShouldBe(4_000);
    }

    [Test]
    public void Interpolation_Midpoint24k_BetweenTinyAndSmall()
    {
        var profile = ResolveFor(24_000);

        profile.MaxHistoryMessages.ShouldBe(10);
        profile.MaxToolsForProvider.ShouldBe(14);
        profile.MaxPinnedMemories.ShouldBe(4);
        profile.MaxMemoriesPerTurn.ShouldBe(3);
        profile.MaxToolResultChars.ShouldBe(3_000);
    }

    [Test]
    public void Interpolation_Midpoint66k_BetweenSmallAndReference()
    {
        var profile = ResolveFor(66_000);

        profile.MaxHistoryMessages.ShouldBe(16);
        profile.MaxToolsForProvider.ShouldBe(23);
        profile.MaxPinnedMemories.ShouldBe(8);
        profile.MaxMemoriesPerTurn.ShouldBe(4);
        profile.MaxToolResultChars.ShouldBe(6_000);
    }

    [Test]
    public void Interpolation_Midpoint300k_BetweenReferencePlateauAndLarge()
    {
        var profile = ResolveFor(300_000);

        profile.MaxHistoryMessages.ShouldBe(30);
        profile.MaxToolsForProvider.ShouldBe(30);
        profile.MaxPinnedMemories.ShouldBe(13);
        profile.MaxMemoriesPerTurn.ShouldBe(7);
        profile.MaxToolResultChars.ShouldBe(8_000);
    }

    [Test]
    public void LargeAnchor_At400k_MatchesCalibratedCeiling()
    {
        var profile = ResolveFor(400_000);

        profile.MaxHistoryMessages.ShouldBe(40);
        profile.MaxToolsForProvider.ShouldBe(30);
        profile.MaxPinnedMemories.ShouldBe(15);
        profile.MaxMemoriesPerTurn.ShouldBe(8);
        profile.MaxToolResultChars.ShouldBe(8_000);
    }

    [TestCase(500_000)]
    [TestCase(1_000_000)]
    [TestCase(10_000_000)]
    public void AboveLargeAnchor_ClampsFlatAtLargeProfile_AndNeverRaisesToolsAboveCeiling(int effectiveInputLimit)
    {
        var profile = ResolveFor(effectiveInputLimit);

        profile.MaxHistoryMessages.ShouldBe(40);
        profile.MaxToolsForProvider.ShouldBe(30);
        profile.MaxPinnedMemories.ShouldBe(15);
        profile.MaxMemoriesPerTurn.ShouldBe(8);
        profile.MaxToolResultChars.ShouldBe(8_000);
    }

    [Test]
    public void Resolve_UsesProviderEffectiveInputLimit_NotModelContextWindowDirectly()
    {
        // Iron rule regression guard: a model with a large nominal ContextWindow must still get the
        // Tiny profile when the provider reports a small EFFECTIVE limit (e.g. Anthropic capping a
        // 1M-context model to 200k without the beta header enabled) — Resolve must never read
        // model.ContextWindow itself.
        var modelWithLargeNominalWindow = new LLMModel { ContextWindow = 1_000_000 };
        _provider.GetEffectiveInputTokenLimit(Arg.Any<LLMModel>()).Returns(16_000);

        var profile = _sut.Resolve(_provider, modelWithLargeNominalWindow);

        profile.MaxHistoryMessages.ShouldBe(8);
        profile.MaxToolsForProvider.ShouldBe(12);
    }
}
