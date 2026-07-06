// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for GeminiEmbeddingProvider — verifies the batchEmbedContents request shape (model,
/// outputDimensionality, taskType per call kind), the x-goog-api-key header, that the response's
/// "embeddings" array is trusted in request order (Gemini's documented contract, unlike OpenAI's
/// index-tagged array), that the result is L2-normalized, and that a missing provider key / HTTP
/// error throws (caught upstream by the semantic-recipe-match try/catch) instead of degrading silently.
/// </summary>

using System.Net;
using System.Text.Json;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Infrastructure.Api;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Api;

[TestFixture]
public class GeminiEmbeddingProviderTests
{
    private CapturingHandler _handler = null!;
    private HttpClient _httpClient = null!;
    private ILlmProviderCredentialReader _credentialReader = null!;
    private GeminiEmbeddingProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _handler = new CapturingHandler();
        _httpClient = new HttpClient(_handler, disposeHandler: false);
        _credentialReader = Substitute.For<ILlmProviderCredentialReader>();
        _credentialReader.GetApiKeyAsync("google", Arg.Any<CancellationToken>()).Returns("gemini-secret-key");
        _provider = new GeminiEmbeddingProvider(_httpClient, _credentialReader);
    }

    [TearDown]
    public void TearDown()
    {
        _httpClient.Dispose();
        _handler.Dispose();
    }

    [Test]
    public void Dimension_MatchesKnowledgeIndexConstant()
    {
        _provider.Dimension.ShouldBe(KnowledgeIndexConstants.EmbeddingDimension);
    }

    [Test]
    public async Task EmbedAsync_SendsApiKeyHeaderAndModelAndOutputDimensionality()
    {
        _handler.ResponseBody = BatchResponse([[0f, 1f]]);

        await _provider.EmbedAsync("hello", CancellationToken.None);

        _handler.LastApiKeyHeader.ShouldBe("gemini-secret-key");
        using var body = JsonDocument.Parse(_handler.LastRequestBody!);
        var firstRequest = body.RootElement.GetProperty("requests")[0];
        firstRequest.GetProperty("model").GetString().ShouldBe("models/gemini-embedding-001");
        firstRequest.GetProperty("embedContentConfig").GetProperty("outputDimensionality")
            .GetInt32().ShouldBe(KnowledgeIndexConstants.EmbeddingDimension);
    }

    [Test]
    public async Task EmbedAsync_UsesRetrievalDocumentTaskType()
    {
        _handler.ResponseBody = BatchResponse([[1f, 0f]]);

        await _provider.EmbedAsync("a document", CancellationToken.None);

        using var body = JsonDocument.Parse(_handler.LastRequestBody!);
        body.RootElement.GetProperty("requests")[0].GetProperty("embedContentConfig")
            .GetProperty("taskType").GetString().ShouldBe("RETRIEVAL_DOCUMENT");
    }

    [Test]
    public async Task EmbedQueryAsync_UsesRetrievalQueryTaskType()
    {
        _handler.ResponseBody = BatchResponse([[1f, 0f]]);

        await _provider.EmbedQueryAsync("what time is it", CancellationToken.None);

        using var body = JsonDocument.Parse(_handler.LastRequestBody!);
        body.RootElement.GetProperty("requests")[0].GetProperty("embedContentConfig")
            .GetProperty("taskType").GetString().ShouldBe("RETRIEVAL_QUERY");
    }

    [Test]
    public async Task EmbedAsync_L2NormalizesTheReturnedVector()
    {
        _handler.ResponseBody = BatchResponse([[3f, 4f]]);

        var result = await _provider.EmbedAsync("hello", CancellationToken.None);

        result[0].ShouldBe(0.6f, 0.0001f);
        result[1].ShouldBe(0.8f, 0.0001f);
    }

    [Test]
    public async Task EmbedAsync_ApiIgnoresOutputDimensionalityHint_TruncatesAndNormalizesLocally()
    {
        // Regression guard: live testing (2026-07-06) found the Gemini API ignores
        // embedContentConfig.outputDimensionality on batchEmbedContents and returns the full
        // 3072-dim gemini-embedding-001 vector regardless — the provider must not trust the server
        // to truncate. Uses a vector longer than EmbeddingDimension (384) with a distinctive value
        // right at the cut boundary so a wrong slice length would be caught.
        var oversized = new float[3072];
        oversized[0] = 3f;
        oversized[1] = 4f;
        oversized[KnowledgeIndexConstants.EmbeddingDimension] = 999f; // must be dropped by truncation
        _handler.ResponseBody = BatchResponse([oversized]);

        var result = await _provider.EmbedAsync("hello", CancellationToken.None);

        result.Length.ShouldBe(KnowledgeIndexConstants.EmbeddingDimension);
        result[0].ShouldBe(0.6f, 0.0001f);
        result[1].ShouldBe(0.8f, 0.0001f);
    }

    [Test]
    public async Task EmbedBatchAsync_TrustsResponseOrder()
    {
        // Gemini's documented contract: "embeddings" is returned in the same order as the request,
        // unlike OpenAI's index-tagged array — no reordering should happen.
        _handler.ResponseBody = BatchResponse([[1f, 0f], [0f, 1f]]);

        var result = await _provider.EmbedBatchAsync(["first", "second"], CancellationToken.None);

        result[0].ShouldBe([1f, 0f]);
        result[1].ShouldBe([0f, 1f]);
    }

    [Test]
    public async Task EmbedAsync_NoProviderKeyConfigured_Throws()
    {
        _credentialReader.GetApiKeyAsync("google", Arg.Any<CancellationToken>()).Returns((string?)null);

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await _provider.EmbedAsync("hello", CancellationToken.None));
    }

    [Test]
    public async Task EmbedAsync_ApiReturnsError_Throws()
    {
        _handler.ResponseStatus = HttpStatusCode.Unauthorized;
        _handler.ResponseBody = """{"error":"invalid api key"}""";

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await _provider.EmbedAsync("hello", CancellationToken.None));
    }

    [Test]
    public async Task EmbedBatchAsync_EmptyInput_ReturnsEmptyWithoutCallingApi()
    {
        var result = await _provider.EmbedBatchAsync([], CancellationToken.None);

        result.ShouldBeEmpty();
        _handler.CallCount.ShouldBe(0);
    }

    [Test]
    public async Task EmbedBatchAsync_MoreThanChunkSize_ReassemblesAcrossChunksInOrder()
    {
        // EmbeddingBatchSize is 16 — 20 texts forces two HTTP calls (16 + 4). Each queued response
        // covers exactly one chunk, so a wrong offset/Array.Copy in the reassembly would either
        // throw (length mismatch) or silently mix up which chunk's vectors land at which index.
        // Vectors are [i+1, 1] rather than raw index values because EmbedManyAsync L2-normalizes
        // the result — the ratio of the two components survives normalization and still uniquely
        // identifies i, whereas comparing a raw component directly would not.
        const int textCount = 20;
        var texts = Enumerable.Range(0, textCount).Select(i => $"text-{i}").ToArray();
        _handler.QueuedResponseBodies.Enqueue(BatchResponse(
            Enumerable.Range(0, 16).Select(i => new[] { (float)(i + 1), 1f }).ToArray()));
        _handler.QueuedResponseBodies.Enqueue(BatchResponse(
            Enumerable.Range(16, 4).Select(i => new[] { (float)(i + 1), 1f }).ToArray()));

        var result = await _provider.EmbedBatchAsync(texts, CancellationToken.None);

        _handler.CallCount.ShouldBe(2);
        for (var i = 0; i < textCount; i++)
        {
            (result[i][0] / result[i][1]).ShouldBe(i + 1f, 0.001f);
        }
    }

    private static string BatchResponse(float[][] vectors) =>
        JsonSerializer.Serialize(new { embeddings = vectors.Select(v => new { values = v }) });

    private sealed class CapturingHandler : HttpMessageHandler
    {
        public HttpStatusCode ResponseStatus { get; set; } = HttpStatusCode.OK;
        public string ResponseBody { get; set; } = "{}";
        public Queue<string> QueuedResponseBodies { get; } = new();
        public string? LastApiKeyHeader { get; private set; }
        public string? LastRequestBody { get; private set; }
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            LastApiKeyHeader = request.Headers.TryGetValues("x-goog-api-key", out var values)
                ? values.FirstOrDefault()
                : null;
            LastRequestBody = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);

            var body = QueuedResponseBodies.Count > 0 ? QueuedResponseBodies.Dequeue() : ResponseBody;
            return new HttpResponseMessage(ResponseStatus)
            {
                Content = new StringContent(body)
            };
        }
    }
}
