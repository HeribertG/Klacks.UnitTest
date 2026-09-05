// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text.Json;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Klacks.Api.KnowledgeIndex.Domain;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.KnowledgeIndex;

/// <summary>
/// Guards the snapshot file shipped with Klacks.Api: it must parse, match the active embedding
/// model, and contain only complete, unique vectors. A stale or malformed file is not an error at
/// runtime (the synchronizer falls back to embedding), so this test is the only place that fails loudly.
/// </summary>
[TestFixture]
public class KnowledgeIndexSnapshotFileTests
{
    private const string ApiProjectDirectory = "Klacks.Api";
    private const string ProjectFileName = "Klacks.Api.csproj";
    private const int MinimumEntries = 100;
    private const int Sha256Length = 32;

    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private static readonly string ExpectedSpaceId =
        $"{KnowledgeIndexConstants.LocalEmbeddingSpacePrefix}{KnowledgeIndexConstants.EmbeddingModelName}@{KnowledgeIndexConstants.EmbeddingDimension}";

    private KnowledgeEmbeddingSnapshotDocument _document = null!;

    [OneTimeSetUp]
    public void LoadDocument()
    {
        var path = Path.Combine(LocateApiProject(), KnowledgeIndexConstants.SnapshotFileRelativePath);
        File.Exists(path).ShouldBeTrue($"Snapshot file missing: {path}. Run scripts/export-knowledge-index-snapshot.ps1 against a fresh database.");

        using var stream = File.OpenRead(path);
        _document = JsonSerializer.Deserialize<KnowledgeEmbeddingSnapshotDocument>(stream, JsonOptions)
            ?? throw new InvalidDataException($"Snapshot file {path} deserialized to null.");
    }

    [Test]
    public void ShippedSnapshot_MatchesCurrentFormatAndModel()
    {
        _document.FormatVersion.ShouldBe(KnowledgeIndexConstants.SnapshotFormatVersion);
        _document.EmbeddingSpaceId.ShouldBe(ExpectedSpaceId);
        _document.Dimension.ShouldBe(KnowledgeIndexConstants.EmbeddingDimension);
    }

    [Test]
    public void ShippedSnapshot_ContainsCompleteUniqueVectors()
    {
        _document.Entries.Count.ShouldBeGreaterThanOrEqualTo(MinimumEntries);

        foreach (var entry in _document.Entries)
        {
            entry.SourceId.ShouldNotBeNullOrWhiteSpace();
            KnowledgeEmbeddingCodec.FromHex(entry.TextHash).Length.ShouldBe(Sha256Length, $"{entry.SourceId}: text hash is not SHA-256");
            var vector = KnowledgeEmbeddingCodec.DecodeVector(entry.Embedding);
            vector.Length.ShouldBe(KnowledgeIndexConstants.EmbeddingDimension, $"{entry.SourceId}: vector length");
            vector.Any(v => v != 0f).ShouldBeTrue($"{entry.SourceId}: vector is all zeros");
        }

        _document.Entries.Select(e => e.TextHash).Distinct(StringComparer.Ordinal).Count().ShouldBe(_document.Entries.Count);
        _document.Entries.Select(e => (e.Kind, e.SourceId)).Distinct().Count().ShouldBe(_document.Entries.Count);
    }


    private static string LocateApiProject()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory != null)
        {
            var candidate = Path.Combine(directory.FullName, ApiProjectDirectory);
            if (File.Exists(Path.Combine(candidate, ProjectFileName)))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new DirectoryNotFoundException(
            $"Could not locate the {ApiProjectDirectory} project by walking up from the test base directory.");
    }
}
