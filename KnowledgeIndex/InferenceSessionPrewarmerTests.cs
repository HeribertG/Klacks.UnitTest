// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The panel-open prewarm rebuilds released ONNX sessions while the user is still typing, so the first question
/// after an idle window does not pay the cold start. It must stay a no-op when nothing is cold or nothing exists,
/// run at most once at a time, and never let a failure escape.
/// </summary>

using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.KnowledgeIndex;

[TestFixture]
public class InferenceSessionPrewarmerTests
{
    private IEmbeddingProvider _embedding = null!;
    private IRerankerProvider _reranker = null!;
    private IServiceScopeFactory _scopeFactory = null!;
    private IHostApplicationLifetime _lifetime = null!;

    [SetUp]
    public void SetUp()
    {
        _embedding = Substitute.For<IEmbeddingProvider>();
        _reranker = Substitute.For<IRerankerProvider>();

        var provider = Substitute.For<IServiceProvider>();
        provider.GetService(typeof(IEmbeddingProvider)).Returns(_embedding);
        provider.GetService(typeof(IRerankerProvider)).Returns(_reranker);
        var scope = Substitute.For<IServiceScope>();
        scope.ServiceProvider.Returns(provider);
        _scopeFactory = Substitute.For<IServiceScopeFactory>();
        _scopeFactory.CreateScope().Returns(scope);

        _lifetime = Substitute.For<IHostApplicationLifetime>();
        _lifetime.ApplicationStopping.Returns(CancellationToken.None);
    }

    [Test]
    public void RequestPrewarm_EverySessionLoaded_DoesNothing()
    {
        var prewarmer = Create(Session(loaded: true), Session(loaded: true));

        prewarmer.RequestPrewarm().ShouldBeFalse();
        _scopeFactory.DidNotReceive().CreateScope();
    }

    [Test]
    public void RequestPrewarm_NoOnnxSessions_DoesNothing()
    {
        var prewarmer = Create();

        prewarmer.RequestPrewarm().ShouldBeFalse();
        _scopeFactory.DidNotReceive().CreateScope();
    }

    [Test]
    public async Task PrewarmAsync_ColdSession_BuildsEmbedderAndReranker()
    {
        var prewarmer = Create(Session(loaded: false));

        await prewarmer.PrewarmAsync();

        await _embedding.Received(1).EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>());
        await _reranker.Received(1).EnsureLoadedAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RequestPrewarm_WhileARebuildRuns_IsANoOp_AndAllowedAgainAfterwards()
    {
        var gate = new TaskCompletionSource<float[]>(TaskCreationOptions.RunContinuationsAsynchronously);
        _embedding.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(gate.Task);
        var prewarmer = Create(Session(loaded: false));

        prewarmer.RequestPrewarm().ShouldBeTrue();
        prewarmer.RequestPrewarm().ShouldBeFalse();

        gate.SetResult([]);
        await WaitUntilAsync(() => _reranker.ReceivedCalls().Any());
        await WaitUntilAsync(() => prewarmer.RequestPrewarm());
    }

    [Test]
    public async Task PrewarmAsync_ProviderThrows_IsSwallowed()
    {
        _embedding.EmbedQueryAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new InvalidOperationException("model file missing"));
        var prewarmer = Create(Session(loaded: false));

        await Should.NotThrowAsync(prewarmer.PrewarmAsync);
    }

    private InferenceSessionPrewarmer Create(params IUnloadableInferenceSession[] sessions) =>
        new(sessions, _scopeFactory, _lifetime, Substitute.For<ILogger<InferenceSessionPrewarmer>>());

    private static IUnloadableInferenceSession Session(bool loaded)
    {
        var session = Substitute.For<IUnloadableInferenceSession>();
        session.IsLoaded.Returns(loaded);
        return session;
    }

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!condition())
        {
            DateTime.UtcNow.ShouldBeLessThan(deadline, "condition not reached within 5 s");
            await Task.Delay(10);
        }
    }
}
