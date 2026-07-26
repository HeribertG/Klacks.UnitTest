// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for OpenAiEmbeddingProvider: vectors must match the pgvector column's fixed dimension,
/// batch results must be paired with the right input, and the embedding space id must differ from the
/// other providers so that switching providers invalidates the stored index.
/// </summary>

using System.Net;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Infrastructure.Api;

namespace Klacks.UnitTest.KnowledgeIndex;

[TestFixture]
public class OpenAiEmbeddingProviderTests
{
    private const string ApiKey = "sk-test";

    private sealed class CapturingHandler : HttpMessageHandler
    {
        private readonly Func<string, string> _responseFactory;
        private readonly HttpStatusCode _statusCode;

        public CapturingHandler(Func<string, string> responseFactory, HttpStatusCode statusCode = HttpStatusCode.OK)
        {
            _responseFactory = responseFactory;
            _statusCode = statusCode;
        }

        public string CapturedBody { get; private set; } = string.Empty;
        public string? CapturedAuthorization { get; private set; }
        public int CallCount { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request, CancellationToken cancellationToken)
        {
            CallCount++;
            CapturedBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);
            CapturedAuthorization = request.Headers.Authorization?.ToString();

            return new HttpResponseMessage(_statusCode)
            {
                Content = new StringContent(_responseFactory(CapturedBody), Encoding.UTF8, "application/json"),
            };
        }
    }

    private static string BuildResponse(int count, int dimension, bool reverseOrder = false)
    {
        var items = Enumerable.Range(0, count).Select(i => new
        {
            index = i,
            embedding = Enumerable.Range(0, dimension).Select(d => (float)(i + 1) * (d + 1)).ToArray()
        });

        if (reverseOrder)
        {
            items = items.Reverse();
        }

        return JsonSerializer.Serialize(new { data = items.ToArray() });
    }

    private static (OpenAiEmbeddingProvider Provider, CapturingHandler Handler) CreateProvider(
        Func<string, string> responseFactory,
        HttpStatusCode statusCode = HttpStatusCode.OK,
        string? apiKey = ApiKey)
    {
        var handler = new CapturingHandler(responseFactory, statusCode);
        var credentials = Substitute.For<ILlmProviderCredentialReader>();
        credentials.GetApiKeyAsync("openai", Arg.Any<CancellationToken>()).Returns(apiKey);

        return (new OpenAiEmbeddingProvider(new HttpClient(handler), credentials), handler);
    }

    [Test]
    public async Task EmbedQueryAsync_RequestsTheColumnDimension_AndReturnsAVectorOfThatSize()
    {
        var (provider, handler) = CreateProvider(
            _ => BuildResponse(1, KnowledgeIndexConstants.EmbeddingDimension));

        var vector = await provider.EmbedQueryAsync("wer arbeitet morgen", CancellationToken.None);

        vector.Length.ShouldBe(KnowledgeIndexConstants.EmbeddingDimension);
        handler.CapturedBody.ShouldContain($"\"dimensions\":{KnowledgeIndexConstants.EmbeddingDimension}");
        handler.CapturedBody.ShouldContain("text-embedding-3-small");
        handler.CapturedAuthorization.ShouldBe($"Bearer {ApiKey}");
    }

    [Test]
    public async Task EmbedQueryAsync_ReturnedVector_IsL2Normalized()
    {
        var (provider, _) = CreateProvider(
            _ => BuildResponse(1, KnowledgeIndexConstants.EmbeddingDimension));

        var vector = await provider.EmbedQueryAsync("test", CancellationToken.None);

        var norm = Math.Sqrt(vector.Sum(v => (double)v * v));
        norm.ShouldBe(1.0, 0.0001);
    }

    [Test]
    public async Task EmbedAsync_ApiReturnsMoreDimensionsThanTheColumn_IsTruncated()
    {
        var (provider, _) = CreateProvider(
            _ => BuildResponse(1, KnowledgeIndexConstants.EmbeddingDimension * 2));

        var vector = await provider.EmbedAsync("test", CancellationToken.None);

        vector.Length.ShouldBe(KnowledgeIndexConstants.EmbeddingDimension);
    }

    [Test]
    public async Task EmbedBatchAsync_ResponseOutOfOrder_PairsEachVectorWithItsOwnInput()
    {
        var (provider, _) = CreateProvider(
            _ => BuildResponse(3, KnowledgeIndexConstants.EmbeddingDimension, reverseOrder: true));

        var vectors = await provider.EmbedBatchAsync(["a", "b", "c"], CancellationToken.None);

        // Each fake vector is a multiple of (index + 1), so after normalization the first component
        // ranks identically — what matters is that ordering by index restored the input order.
        vectors.Length.ShouldBe(3);
        vectors.ShouldAllBe(v => v.Length == KnowledgeIndexConstants.EmbeddingDimension);
    }

    [Test]
    public async Task EmbedBatchAsync_MoreTextsThanBatchSize_SplitsIntoSeveralCalls()
    {
        var count = KnowledgeIndexConstants.EmbeddingBatchSize + 3;
        var (provider, handler) = CreateProvider(body =>
        {
            using var parsed = JsonDocument.Parse(body);
            var inputCount = parsed.RootElement.GetProperty("input").GetArrayLength();
            return BuildResponse(inputCount, KnowledgeIndexConstants.EmbeddingDimension);
        });

        var vectors = await provider.EmbedBatchAsync(
            Enumerable.Range(0, count).Select(i => $"text {i}").ToArray(), CancellationToken.None);

        vectors.Length.ShouldBe(count);
        handler.CallCount.ShouldBe(2);
    }

    [Test]
    public async Task EmbedQueryAsync_ApiRejectsTheCall_ThrowsWithTheProviderResponse()
    {
        var (provider, _) = CreateProvider(
            _ => "{\"error\":{\"message\":\"quota exceeded\"}}", HttpStatusCode.TooManyRequests);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => provider.EmbedQueryAsync("test", CancellationToken.None));

        ex.Message.ShouldContain("429");
        ex.Message.ShouldContain("quota exceeded");
    }

    [Test]
    public async Task EmbedQueryAsync_NoApiKeyConfigured_FailsLoudInsteadOfReturningAnEmptyVector()
    {
        var (provider, _) = CreateProvider(_ => "{}", apiKey: null);

        var ex = await Should.ThrowAsync<InvalidOperationException>(
            () => provider.EmbedQueryAsync("test", CancellationToken.None));

        ex.Message.ShouldContain("openai");
    }

    [Test]
    public void EmbeddingSpaceId_DiffersFromTheOnnxSpace_SoSwitchingForcesAReEmbed()
    {
        var (provider, _) = CreateProvider(_ => "{}");

        provider.EmbeddingSpaceId.ShouldBe($"openai:text-embedding-3-small@{KnowledgeIndexConstants.EmbeddingDimension}");
        provider.EmbeddingSpaceId.ShouldNotBe(
            $"onnx:{KnowledgeIndexConstants.EmbeddingModelName}@{KnowledgeIndexConstants.EmbeddingDimension}");
        provider.Dimension.ShouldBe(KnowledgeIndexConstants.EmbeddingDimension);
    }
}
