// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Security.Cryptography;
using System.Text;
using Klacks.Api.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex;

[TestFixture]
public class KnowledgeEmbeddingSnapshotExporterTests
{
    private const int Dimension = 4;
    private const string SpaceId = "onnx:test-space@4";

    private IKnowledgeIndexRepository _repository = null!;
    private IEmbeddingProvider _embeddingProvider = null!;
    private ISkillPhraseRepository _phrases = null!;
    private KnowledgeEmbeddingSnapshotExporter _sut = null!;

    [SetUp]
    public void Setup()
    {
        _repository = Substitute.For<IKnowledgeIndexRepository>();
        _embeddingProvider = Substitute.For<IEmbeddingProvider>();
        _embeddingProvider.Dimension.Returns(Dimension);
        _embeddingProvider.EmbeddingSpaceId.Returns(SpaceId);
        _phrases = Substitute.For<ISkillPhraseRepository>();
        _phrases.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SkillPhrase>)new List<SkillPhrase>());
        _sut = new KnowledgeEmbeddingSnapshotExporter(_repository, _embeddingProvider, _phrases);
    }

    [Test]
    public async Task ExportAsync_RefusesWhenLearnedPhrasesExist()
    {
        _phrases.GetAllActiveAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<SkillPhrase>)new List<SkillPhrase>
            {
                new() { Source = SkillPhraseSources.Seed, Phrase = "seeded" },
                new() { Source = SkillPhraseSources.Learned, Phrase = "learned" }
            });

        var ex = await Should.ThrowAsync<InvalidOperationException>(() => _sut.ExportAsync(CancellationToken.None));

        ex.Message.ShouldContain("1 learned phrases");
        await _repository.DidNotReceive().GetAllWithEmbeddingsAsync(Arg.Any<CancellationToken>());
    }

    private static byte[] HashFor(string text) => SHA256.HashData(Encoding.UTF8.GetBytes(text));

    private static KnowledgeEntry Entry(
        KnowledgeEntryKind kind,
        string sourceId,
        float[] embedding,
        string? hashSeed = null) =>
        new()
        {
            Id = Guid.NewGuid(),
            Kind = kind,
            SourceId = sourceId,
            Text = sourceId,
            TextHash = HashFor(hashSeed ?? sourceId),
            Embedding = embedding,
            UpdatedAt = DateTime.UtcNow
        };

    [Test]
    public async Task ExportAsync_SkipsZeroVectorEntry()
    {
        var zero = Entry(KnowledgeEntryKind.Skill, "zero_skill", new float[Dimension]);
        var valid = Entry(KnowledgeEntryKind.Skill, "valid_skill", [1f, 2f, 3f, 4f]);
        _repository.GetAllWithEmbeddingsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<KnowledgeEntry>)new List<KnowledgeEntry> { zero, valid });

        var document = await _sut.ExportAsync(CancellationToken.None);

        document.Entries.Count.ShouldBe(1);
        document.Entries[0].SourceId.ShouldBe("valid_skill");
    }

    [Test]
    public async Task ExportAsync_SkipsEntryWithWrongVectorLength()
    {
        var wrongLength = Entry(KnowledgeEntryKind.Skill, "short_skill", [1f, 2f]);
        var valid = Entry(KnowledgeEntryKind.Skill, "valid_skill", [1f, 2f, 3f, 4f]);
        _repository.GetAllWithEmbeddingsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<KnowledgeEntry>)new List<KnowledgeEntry> { wrongLength, valid });

        var document = await _sut.ExportAsync(CancellationToken.None);

        document.Entries.Count.ShouldBe(1);
        document.Entries[0].SourceId.ShouldBe("valid_skill");
    }

    [Test]
    public async Task ExportAsync_SetsDocumentMetadata()
    {
        _repository.GetAllWithEmbeddingsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<KnowledgeEntry>)new List<KnowledgeEntry>());

        var document = await _sut.ExportAsync(CancellationToken.None);

        document.FormatVersion.ShouldBe(KnowledgeIndexConstants.SnapshotFormatVersion);
        document.EmbeddingSpaceId.ShouldBe(SpaceId);
        document.Dimension.ShouldBe(Dimension);
        document.SourceVersion.ShouldBe($"{VersionConstant.CMajor}.{VersionConstant.CMinor}.{VersionConstant.CPatch}");
        document.CreatedAt.ShouldBeInRange(DateTime.UtcNow.AddMinutes(-1), DateTime.UtcNow.AddMinutes(1));
    }

    [Test]
    public async Task ExportAsync_EncodesHashAndEmbeddingRoundtrippably()
    {
        var vector = new[] { 0.1f, -2.5f, 3.75f, 100f };
        var entry = Entry(KnowledgeEntryKind.Skill, "encoded_skill", vector, "encoded_skill_text");
        _repository.GetAllWithEmbeddingsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<KnowledgeEntry>)new List<KnowledgeEntry> { entry });

        var document = await _sut.ExportAsync(CancellationToken.None);

        var snapshotEntry = document.Entries.Single();
        snapshotEntry.Kind.ShouldBe((short)KnowledgeEntryKind.Skill);
        snapshotEntry.SourceId.ShouldBe("encoded_skill");

        KnowledgeEmbeddingCodec.FromHex(snapshotEntry.TextHash).ShouldBe(entry.TextHash);
        KnowledgeEmbeddingCodec.DecodeVector(snapshotEntry.Embedding).ShouldBe(vector);
    }

    [Test]
    public async Task ExportAsync_SortsEntriesByKindThenSourceId()
    {
        var recipeB = Entry(KnowledgeEntryKind.Recipe, "recipe_b", [1f, 1f, 1f, 1f]);
        var skillZ = Entry(KnowledgeEntryKind.Skill, "skill_z", [1f, 1f, 1f, 1f]);
        var skillA = Entry(KnowledgeEntryKind.Skill, "skill_a", [1f, 1f, 1f, 1f]);
        var recipeA = Entry(KnowledgeEntryKind.Recipe, "recipe_a", [1f, 1f, 1f, 1f]);
        _repository.GetAllWithEmbeddingsAsync(Arg.Any<CancellationToken>())
            .Returns((IReadOnlyList<KnowledgeEntry>)new List<KnowledgeEntry> { recipeB, skillZ, skillA, recipeA });

        var document = await _sut.ExportAsync(CancellationToken.None);

        document.Entries.Select(e => e.SourceId).ShouldBe(["skill_a", "skill_z", "recipe_a", "recipe_b"]);
    }
}
