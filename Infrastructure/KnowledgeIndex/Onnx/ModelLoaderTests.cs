// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Security.Cryptography;
using Shouldly;
using Klacks.Api.Infrastructure.KnowledgeIndex.Infrastructure.Onnx;
using NUnit.Framework;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex.Onnx;

[TestFixture]
public class ModelLoaderTests
{
    private string _tempDir = null!;

    [SetUp]
    public void Setup()
    {
        _tempDir = Path.Combine(Path.GetTempPath(), Guid.NewGuid().ToString());
        Directory.CreateDirectory(_tempDir);
    }

    [TearDown]
    public void TearDown() => Directory.Delete(_tempDir, recursive: true);

    [Test]
    public async Task EnsureFileAsync_WhenFileMatchesHash_DoesNotRedownload()
    {
        var filePath = Path.Combine(_tempDir, "model.onnx");
        var content = new byte[] { 1, 2, 3, 4 };
        await File.WriteAllBytesAsync(filePath, content);
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        var downloaded = false;
        var loader = new ModelLoader(new HttpClient(new StubHandler(() => { downloaded = true; return content; })));

        await loader.EnsureFileAsync(filePath, "https://example/model.onnx", hash, CancellationToken.None);

        downloaded.ShouldBeFalse();
    }

    [Test]
    public async Task EnsureFileAsync_WhenFileAbsent_Downloads()
    {
        var filePath = Path.Combine(_tempDir, "model.onnx");
        var content = new byte[] { 5, 6, 7, 8 };
        var hash = Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();

        var downloaded = false;
        var loader = new ModelLoader(new HttpClient(new StubHandler(() => { downloaded = true; return content; })));

        await loader.EnsureFileAsync(filePath, "https://example/model.onnx", hash, CancellationToken.None);

        downloaded.ShouldBeTrue();
        File.Exists(filePath).ShouldBeTrue();
    }

    [Test]
    public async Task EnsureFileAsync_WhenFileHashMismatch_Redownloads()
    {
        var filePath = Path.Combine(_tempDir, "model.onnx");
        var staleContent = new byte[] { 0, 0, 0, 0 };
        await File.WriteAllBytesAsync(filePath, staleContent);

        var freshContent = new byte[] { 9, 10, 11, 12 };
        var hash = Convert.ToHexString(SHA256.HashData(freshContent)).ToLowerInvariant();

        var downloaded = false;
        var loader = new ModelLoader(new HttpClient(new StubHandler(() => { downloaded = true; return freshContent; })));

        await loader.EnsureFileAsync(filePath, "https://example/model.onnx", hash, CancellationToken.None);

        downloaded.ShouldBeTrue();
    }

    [Test]
    public async Task EnsureFileAsync_WhenDownloadHashMismatch_ThrowsAndDeletesTemp()
    {
        var filePath = Path.Combine(_tempDir, "model.onnx");
        var content = new byte[] { 1, 2, 3 };
        var wrongHash = "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa";

        var loader = new ModelLoader(new HttpClient(new StubHandler(() => content)));

        Func<Task> act = () => loader.EnsureFileAsync(filePath, "https://example/model.onnx", wrongHash, CancellationToken.None);

        await act.ShouldThrowAsync<InvalidOperationException>();
        File.Exists(filePath).ShouldBeFalse();
    }

    private sealed class StubHandler : HttpMessageHandler
    {
        private readonly Func<byte[]> _onGet;
        public StubHandler(Func<byte[]> onGet) => _onGet = onGet;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
            => Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.OK) { Content = new ByteArrayContent(_onGet()) });
    }
}
