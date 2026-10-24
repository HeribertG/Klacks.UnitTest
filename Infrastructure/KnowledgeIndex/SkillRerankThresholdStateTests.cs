// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// SkillRerankThresholdState is the live threshold every skill pass reads. Whatever the configuration or the tuner
/// hands it, it stays inside the fixed bounds; only Adaptive mode learns.
/// </summary>

using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Services;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex;

[TestFixture]
public class SkillRerankThresholdStateTests
{
    [TestCase(SkillRerankMode.Adaptive, true)]
    [TestCase(SkillRerankMode.Always, false)]
    [TestCase(SkillRerankMode.Never, false)]
    public void OnlyAdaptiveLearns(SkillRerankMode mode, bool learned)
    {
        new SkillRerankThresholdState(new SkillRerankOptions(mode, 0.03)).IsLearned.ShouldBe(learned);
    }

    [TestCase(0.05, 0.05)]
    [TestCase(-1.0, KnowledgeIndexConstants.MinSkillRerankMarginThreshold)]
    [TestCase(9.0, KnowledgeIndexConstants.MaxSkillRerankMarginThreshold)]
    [TestCase(double.NaN, KnowledgeIndexConstants.DefaultSkillRerankMarginThreshold)]
    public void Set_StaysInsideTheBounds(double value, double expected)
    {
        var state = new SkillRerankThresholdState(new SkillRerankOptions(SkillRerankMode.Adaptive, 0.03));

        state.Set(value);

        state.Current.ShouldBe(expected, tolerance: 1e-12);
    }

    [Test]
    public void ConfiguredValues_AreClampedToo()
    {
        var state = new SkillRerankThresholdState(
            new SkillRerankOptions(SkillRerankMode.Adaptive, 5.0) { ExplorationRate = 0.9 });

        state.Current.ShouldBe(KnowledgeIndexConstants.MaxSkillRerankMarginThreshold);
        state.ExplorationRate.ShouldBe(KnowledgeIndexConstants.MaxSkillRerankExplorationRate);
    }
}
