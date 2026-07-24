// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the deterministic memory-relation candidate scoring (P5 population): shared tags
/// at or above the minimum count, and embedding cosine similarity at or above the threshold, each
/// create a candidate; below either bar no candidate is produced; when both signals fire for the same
/// peer the higher-confidence one wins; results are capped to MaxEdgesPerMemory, best first.
/// </summary>

using Klacks.Api.Application.Services.Assistant.MemoryGraph;

namespace Klacks.UnitTest.Application.Services.Assistant.MemoryGraph;

[TestFixture]
public class MemoryRelationCandidateBuilderTests
{
    private static AgentMemory Memory(Guid id, string[]? tags = null, float[]? embedding = null) => new()
    {
        Id = id,
        Key = id.ToString(),
        Content = "content",
        Embedding = embedding,
        Tags = (tags ?? Array.Empty<string>()).Select(t => new AgentMemoryTag { MemoryId = id, Tag = t }).ToList(),
    };

    [Test]
    public void ComputeCandidates_SharedTag_ProducesCandidate()
    {
        var target = Memory(Guid.NewGuid(), tags: new[] { "spitex" });
        var peer = Memory(Guid.NewGuid(), tags: new[] { "spitex" });

        var candidates = MemoryRelationCandidateBuilder.ComputeCandidates(target, new[] { peer });

        candidates.Count.ShouldBe(1);
        candidates[0].MemoryId.ShouldBe(peer.Id);
        candidates[0].Provenance.ShouldBe(MemoryGraphConstants.SharedTagsProvenance);
        candidates[0].Confidence.ShouldBeGreaterThanOrEqualTo(MemoryGraphConstants.NeighbourMinConfidence);
    }

    [Test]
    public void ComputeCandidates_NoSharedTag_ProducesNoCandidate()
    {
        var target = Memory(Guid.NewGuid(), tags: new[] { "spitex" });
        var peer = Memory(Guid.NewGuid(), tags: new[] { "gastro" });

        var candidates = MemoryRelationCandidateBuilder.ComputeCandidates(target, new[] { peer });

        candidates.ShouldBeEmpty();
    }

    [Test]
    public void ComputeCandidates_VectorSimilarityAboveThreshold_ProducesCandidate()
    {
        var target = Memory(Guid.NewGuid(), embedding: new float[] { 1f, 0f });
        var peer = Memory(Guid.NewGuid(), embedding: new float[] { 1f, 0f });

        var candidates = MemoryRelationCandidateBuilder.ComputeCandidates(target, new[] { peer });

        candidates.Count.ShouldBe(1);
        candidates[0].Provenance.ShouldBe(MemoryGraphConstants.VectorSimilarityProvenance);
        candidates[0].Confidence.ShouldBe(1.0, 0.0001);
    }

    [Test]
    public void ComputeCandidates_VectorSimilarityBelowThreshold_ProducesNoCandidate()
    {
        var target = Memory(Guid.NewGuid(), embedding: new float[] { 1f, 0f });
        var peer = Memory(Guid.NewGuid(), embedding: new float[] { 0f, 1f });

        var candidates = MemoryRelationCandidateBuilder.ComputeCandidates(target, new[] { peer });

        candidates.ShouldBeEmpty();
    }

    [Test]
    public void ComputeCandidates_BothSignalsFire_PicksHigherConfidence()
    {
        var target = Memory(Guid.NewGuid(), tags: new[] { "spitex" }, embedding: new float[] { 1f, 0f });
        var peer = Memory(Guid.NewGuid(), tags: new[] { "spitex" }, embedding: new float[] { 1f, 0f });

        var candidates = MemoryRelationCandidateBuilder.ComputeCandidates(target, new[] { peer });

        candidates.Count.ShouldBe(1);
        candidates[0].Provenance.ShouldBe(MemoryGraphConstants.VectorSimilarityProvenance);
    }

    [Test]
    public void ComputeCandidates_CapsAtMaxEdgesPerMemory_BestFirst()
    {
        var target = Memory(Guid.NewGuid(), tags: new[] { "spitex" });
        var pool = Enumerable.Range(0, MemoryGraphConstants.MaxEdgesPerMemory + 3)
            .Select(_ => Memory(Guid.NewGuid(), tags: new[] { "spitex" }))
            .ToList();

        var candidates = MemoryRelationCandidateBuilder.ComputeCandidates(target, pool);

        candidates.Count.ShouldBe(MemoryGraphConstants.MaxEdgesPerMemory);
    }

    [Test]
    public void ComputeCandidates_ExcludesSelf()
    {
        var target = Memory(Guid.NewGuid(), tags: new[] { "spitex" });

        var candidates = MemoryRelationCandidateBuilder.ComputeCandidates(target, new[] { target });

        candidates.ShouldBeEmpty();
    }
}
