// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text.Json;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.Api.KnowledgeIndex.Infrastructure.Persistence;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex;

[TestFixture]
public class FileKnowledgeEmbeddingSnapshotReaderTests
{
    private const string SpaceId = "onnx:test-model@4";
    private const int Dimension = 4;

    private readonly List<string> _tempFiles = [];

    private ILogger<FileKnowledgeEmbeddingSnapshotReader> _logger = null!;

    [SetUp]
    public void Setup()
    {
        _logger = Substitute.For<ILogger<FileKnowledgeEmbeddingSnapshotReader>>();
    }

    [TearDown]
    public void TearDown()
    {
        foreach (var file in _tempFiles)
        {
            if (File.Exists(file))
            {
                File.Delete(file);
            }
        }

        _tempFiles.Clear();
    }

    [Test]
    public async Task LoadAsync_FileMissing_ReturnsEmpty()
    {
        var path = NewTempPath();

        var reader = new FileKnowledgeEmbeddingSnapshotReader(path, true, _logger);

        (await reader.LoadAsync(SpaceId, Dimension, CancellationToken.None)).Count.ShouldBe(0);
    }

    [Test]
    public async Task LoadAsync_Disabled_ReturnsEmpty()
    {
        var path = WriteSnapshot(NewDocument(Entry("aa", [1f, 2f, 3f, 4f])));

        var reader = new FileKnowledgeEmbeddingSnapshotReader(path, false, _logger);

        (await reader.LoadAsync(SpaceId, Dimension, CancellationToken.None)).Count.ShouldBe(0);
    }

    [Test]
    public async Task LoadAsync_UnparsableFile_ReturnsEmpty()
    {
        var path = NewTempPath();
        await File.WriteAllTextAsync(path, "{ this is not json");

        var reader = new FileKnowledgeEmbeddingSnapshotReader(path, true, _logger);

        (await reader.LoadAsync(SpaceId, Dimension, CancellationToken.None)).Count.ShouldBe(0);
    }

    [Test]
    public async Task LoadAsync_NullEntries_ReturnsEmpty()
    {
        var path = NewTempPath();
        await File.WriteAllTextAsync(
            path,
            $$"""{"formatVersion":{{KnowledgeIndexConstants.SnapshotFormatVersion}},"embeddingSpaceId":"{{SpaceId}}","dimension":{{Dimension}},"entries":null}""");

        var reader = new FileKnowledgeEmbeddingSnapshotReader(path, true, _logger);

        (await reader.LoadAsync(SpaceId, Dimension, CancellationToken.None)).Count.ShouldBe(0);
    }

    [Test]
    public async Task LoadAsync_EmbeddingSpaceMismatch_ReturnsEmpty()
    {
        var document = NewDocument(Entry("aa", [1f, 2f, 3f, 4f]));
        document.EmbeddingSpaceId = "onnx:other-model@4";
        var path = WriteSnapshot(document);

        var reader = new FileKnowledgeEmbeddingSnapshotReader(path, true, _logger);

        (await reader.LoadAsync(SpaceId, Dimension, CancellationToken.None)).Count.ShouldBe(0);
    }

    [Test]
    public async Task LoadAsync_DimensionMismatch_ReturnsEmpty()
    {
        var document = NewDocument(Entry("aa", [1f, 2f, 3f, 4f]));
        document.Dimension = Dimension + 1;
        var path = WriteSnapshot(document);

        var reader = new FileKnowledgeEmbeddingSnapshotReader(path, true, _logger);

        (await reader.LoadAsync(SpaceId, Dimension, CancellationToken.None)).Count.ShouldBe(0);
    }

    [Test]
    public async Task LoadAsync_FormatVersionMismatch_ReturnsEmpty()
    {
        var document = NewDocument(Entry("aa", [1f, 2f, 3f, 4f]));
        document.FormatVersion = KnowledgeIndexConstants.SnapshotFormatVersion + 1;
        var path = WriteSnapshot(document);

        var reader = new FileKnowledgeEmbeddingSnapshotReader(path, true, _logger);

        (await reader.LoadAsync(SpaceId, Dimension, CancellationToken.None)).Count.ShouldBe(0);
    }

    [Test]
    public async Task LoadAsync_ValidFile_ReturnsVectorsKeyedByTextHash()
    {
        var path = WriteSnapshot(NewDocument(
            Entry("aa", [1f, 2f, 3f, 4f]),
            Entry("bb", [5f, 6f, 7f, 8f])));

        var reader = new FileKnowledgeEmbeddingSnapshotReader(path, true, _logger);

        var result = await reader.LoadAsync(SpaceId, Dimension, CancellationToken.None);

        result.Count.ShouldBe(2);
        result["aa"].ShouldBe(new[] { 1f, 2f, 3f, 4f });
        result["bb"].ShouldBe(new[] { 5f, 6f, 7f, 8f });
    }

    [Test]
    public async Task LoadAsync_EntryWithWrongVectorLength_IsSkippedWhileValidOnesRemain()
    {
        var path = WriteSnapshot(NewDocument(
            Entry("aa", [1f, 2f, 3f]),
            Entry("bb", [5f, 6f, 7f, 8f])));

        var reader = new FileKnowledgeEmbeddingSnapshotReader(path, true, _logger);

        var result = await reader.LoadAsync(SpaceId, Dimension, CancellationToken.None);

        result.Count.ShouldBe(1);
        result.ContainsKey("aa").ShouldBeFalse();
        result["bb"].ShouldBe(new[] { 5f, 6f, 7f, 8f });
    }

    [Test]
    public async Task LoadAsync_DuplicateTextHash_KeepsTheFirstEntry()
    {
        var path = WriteSnapshot(NewDocument(
            Entry("aa", [1f, 2f, 3f, 4f]),
            Entry("aa", [9f, 9f, 9f, 9f])));

        var reader = new FileKnowledgeEmbeddingSnapshotReader(path, true, _logger);

        var result = await reader.LoadAsync(SpaceId, Dimension, CancellationToken.None);

        result.Count.ShouldBe(1);
        result["aa"].ShouldBe(new[] { 1f, 2f, 3f, 4f });
    }

    [Test]
    public async Task LoadAsync_UppercaseTextHash_IsStillFoundByLowercaseKey()
    {
        var entry = Entry("aa", [1f, 2f, 3f, 4f]);
        entry.TextHash = "AA";
        var path = WriteSnapshot(NewDocument(entry));

        var reader = new FileKnowledgeEmbeddingSnapshotReader(path, true, _logger);

        var result = await reader.LoadAsync(SpaceId, Dimension, CancellationToken.None);

        result["aa"].ShouldBe(new[] { 1f, 2f, 3f, 4f });
    }

    [Test]
    public async Task LoadAsync_CalledTwice_ReadsTheFileOnlyOnce()
    {
        var path = WriteSnapshot(NewDocument(Entry("aa", [1f, 2f, 3f, 4f])));

        var reader = new FileKnowledgeEmbeddingSnapshotReader(path, true, _logger);

        var first = await reader.LoadAsync(SpaceId, Dimension, CancellationToken.None);
        File.Delete(path);
        var second = await reader.LoadAsync(SpaceId, Dimension, CancellationToken.None);

        first.Count.ShouldBe(1);
        second.Count.ShouldBe(1);
        second["aa"].ShouldBe(new[] { 1f, 2f, 3f, 4f });
    }

    private static KnowledgeEmbeddingSnapshotEntry Entry(string textHash, float[] vector) =>
        new()
        {
            Kind = 0,
            SourceId = textHash,
            TextHash = textHash,
            Embedding = KnowledgeEmbeddingCodec.EncodeVector(vector)
        };

    private static KnowledgeEmbeddingSnapshotDocument NewDocument(params KnowledgeEmbeddingSnapshotEntry[] entries) =>
        new()
        {
            FormatVersion = KnowledgeIndexConstants.SnapshotFormatVersion,
            EmbeddingSpaceId = SpaceId,
            Dimension = Dimension,
            CreatedAt = DateTime.UtcNow,
            SourceVersion = "test",
            Entries = entries.ToList()
        };

    private string NewTempPath()
    {
        var path = Path.Combine(Path.GetTempPath(), $"knowledge-snapshot-{Guid.NewGuid():N}.json");
        _tempFiles.Add(path);
        return path;
    }

    private string WriteSnapshot(KnowledgeEmbeddingSnapshotDocument document)
    {
        var path = NewTempPath();
        File.WriteAllText(path, JsonSerializer.Serialize(document, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        return path;
    }
}
