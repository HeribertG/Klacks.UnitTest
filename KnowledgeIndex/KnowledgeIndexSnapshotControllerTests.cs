// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Reflection;
using System.Text;
using System.Text.Json;
using Klacks.Api.Domain.Constants;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Klacks.Api.KnowledgeIndex.Domain;
using Klacks.Api.Presentation.Controllers.Assistant;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.KnowledgeIndex;

[TestFixture]
public class KnowledgeIndexSnapshotControllerTests
{
    private IKnowledgeEmbeddingSnapshotExporter _exporter = null!;
    private KnowledgeIndexSnapshotController _sut = null!;

    [SetUp]
    public void Setup()
    {
        _exporter = Substitute.For<IKnowledgeEmbeddingSnapshotExporter>();
        _sut = new KnowledgeIndexSnapshotController(_exporter);
    }

    [Test]
    public void Controller_ShouldPinJwtSchemeAndAdminRole()
    {
        var authorizeAttribute = typeof(KnowledgeIndexSnapshotController)
            .GetCustomAttribute<AuthorizeAttribute>();

        authorizeAttribute.ShouldNotBeNull();
        authorizeAttribute!.AuthenticationSchemes.ShouldBe(JwtBearerDefaults.AuthenticationScheme);
        authorizeAttribute.Roles.ShouldBe(Roles.Admin);
    }

    [Test]
    public async Task GetSnapshot_ReturnsConflict_WhenExportIsRefused()
    {
        _exporter.ExportAsync(Arg.Any<CancellationToken>())
            .Returns<KnowledgeEmbeddingSnapshotDocument>(_ => throw new InvalidOperationException("refused"));

        var result = await _sut.GetSnapshot(CancellationToken.None);

        result.ShouldBeOfType<ConflictObjectResult>();
    }

    [Test]
    public async Task GetSnapshot_ReturnsFileContentResultWithExpectedNameAndContentType()
    {
        var document = new KnowledgeEmbeddingSnapshotDocument
        {
            FormatVersion = KnowledgeIndexConstants.SnapshotFormatVersion,
            EmbeddingSpaceId = "onnx:test-space@4",
            Dimension = 4,
            CreatedAt = DateTime.UtcNow,
            SourceVersion = "1.0.23",
            Entries = new List<KnowledgeEmbeddingSnapshotEntry>
            {
                new()
                {
                    Kind = (short)KnowledgeEntryKind.Skill,
                    SourceId = "some_skill",
                    TextHash = KnowledgeEmbeddingCodec.ToHex([1, 2, 3, 4]),
                    Embedding = KnowledgeEmbeddingCodec.EncodeVector([0.1f, 0.2f, 0.3f, 0.4f])
                }
            }
        };
        _exporter.ExportAsync(Arg.Any<CancellationToken>()).Returns(document);

        var result = await _sut.GetSnapshot(CancellationToken.None);

        var fileResult = result.ShouldBeOfType<FileContentResult>();
        fileResult.ContentType.ShouldBe("application/json");
        fileResult.FileDownloadName.ShouldBe(KnowledgeIndexConstants.SnapshotFileName);

        var json = Encoding.UTF8.GetString(fileResult.FileContents);
        var roundtripped = JsonSerializer.Deserialize<KnowledgeEmbeddingSnapshotDocument>(
            json, new JsonSerializerOptions(JsonSerializerDefaults.Web));

        roundtripped.ShouldNotBeNull();
        roundtripped!.FormatVersion.ShouldBe(document.FormatVersion);
        roundtripped.EmbeddingSpaceId.ShouldBe(document.EmbeddingSpaceId);
        roundtripped.Dimension.ShouldBe(document.Dimension);
        roundtripped.SourceVersion.ShouldBe(document.SourceVersion);
        roundtripped.Entries.Count.ShouldBe(1);
        roundtripped.Entries[0].SourceId.ShouldBe("some_skill");
        roundtripped.Entries[0].Kind.ShouldBe((short)KnowledgeEntryKind.Skill);
        roundtripped.Entries[0].TextHash.ShouldBe(document.Entries[0].TextHash);
        roundtripped.Entries[0].Embedding.ShouldBe(document.Entries[0].Embedding);
    }
}
