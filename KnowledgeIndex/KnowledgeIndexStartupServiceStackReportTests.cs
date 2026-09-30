// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Guards the startup report of the active retrieval stack. A silent fallback to a remote embedding
/// provider changes retrieval quality without any visible signal, which once invalidated a whole
/// measurement round, so the warning must not disappear in a refactoring. Also pins that the startup
/// sync goes through the single-flight scheduler, is awaited while the index covers too little of the
/// catalogue and only requested (background) once it covers enough.
/// </summary>

using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Constants;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Klacks.Api.KnowledgeIndex.Application.Services;
using Klacks.Api.KnowledgeIndex.Domain;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.KnowledgeIndex;

[TestFixture]
public class KnowledgeIndexStartupServiceStackReportTests
{
    [Test]
    public async Task StartAsync_WarnsOnce_WhenTheEmbeddingProviderIsNotTheLocalOnnxStack()
    {
        var recorder = await RunWithEmbeddingSpaceAsync("openai:text-embedding-3-small@384");

        recorder.Warnings.ShouldContain(m => m.Contains("FALLBACK"));
        recorder.Warnings.ShouldContain(m => m.Contains("openai:text-embedding-3-small@384"));
    }

    [Test]
    public async Task StartAsync_DoesNotWarn_WhenTheLocalOnnxStackIsActive()
    {
        var recorder = await RunWithEmbeddingSpaceAsync(
            KnowledgeIndexConstants.LocalEmbeddingSpacePrefix + "multilingual-e5-small@384");

        recorder.Warnings.ShouldBeEmpty();
        recorder.Infos.ShouldContain(m => m.Contains("Retrieval stack"));
    }

    [Test]
    public async Task StartAsync_RunsTheSyncThroughTheSchedulerAndWaitsForIt()
    {
        var scheduler = new StubScheduler(succeeds: true);

        await RunWithEmbeddingSpaceAsync(
            KnowledgeIndexConstants.LocalEmbeddingSpacePrefix + "multilingual-e5-small@384", scheduler);

        scheduler.RunNowReasons.ShouldBe([KnowledgeIndexSyncConstants.StartupReason]);
        scheduler.Requests.ShouldBeEmpty();
    }

    [Test]
    public async Task StartAsync_WarnsWithTheError_WhenTheStartupSyncFailed()
    {
        var recorder = await RunWithEmbeddingSpaceAsync(
            KnowledgeIndexConstants.LocalEmbeddingSpacePrefix + "multilingual-e5-small@384",
            new StubScheduler(succeeds: false));

        recorder.Warnings.ShouldContain(m => m.Contains("did not complete") && m.Contains(StubScheduler.FailureMessage));
    }

    [TestCase(0)]
    [TestCase(1)]
    [TestCase(8)]
    public async Task StartAsync_IndexCoversTooLittleOfTheCatalogue_WaitsForTheSync(int storedOfTen)
    {
        var scheduler = new StubScheduler(succeeds: true);

        await RunWithIndexAsync(scheduler, requiredSkills: 10, storedSkills: storedOfTen);

        scheduler.RunNowReasons.ShouldBe([KnowledgeIndexSyncConstants.StartupReason]);
        scheduler.Requests.ShouldBeEmpty();
    }

    [TestCase(9)]
    [TestCase(10)]
    public async Task StartAsync_IndexCoversTheCatalogue_RequestsABackgroundSyncAndDoesNotWait(int storedOfTen)
    {
        var scheduler = new StubScheduler(succeeds: true);

        var recorder = await RunWithIndexAsync(scheduler, requiredSkills: 10, storedSkills: storedOfTen);

        scheduler.Requests.ShouldBe([KnowledgeIndexSyncConstants.StartupReason]);
        scheduler.RunNowReasons.ShouldBeEmpty();
        recorder.Warnings.ShouldBeEmpty();
        recorder.Infos.ShouldContain(m => m.Contains("background"));
    }

    [Test]
    public async Task StartAsync_OrphanRowsDoNotCountAsCoverage()
    {
        var scheduler = new StubScheduler(succeeds: true);

        await RunWithIndexAsync(scheduler, requiredSkills: 10, storedSkills: 1, storedOrphans: 50);

        scheduler.RunNowReasons.ShouldBe([KnowledgeIndexSyncConstants.StartupReason]);
        scheduler.Requests.ShouldBeEmpty();
    }

    [Test]
    public async Task StartAsync_IndexUnreadable_FallsBackToTheBlockingSync()
    {
        var scheduler = new StubScheduler(succeeds: true);

        await RunWithIndexAsync(scheduler, requiredSkills: 10, storedSkills: 10, readFails: true);

        scheduler.RunNowReasons.ShouldBe([KnowledgeIndexSyncConstants.StartupReason]);
        scheduler.Requests.ShouldBeEmpty();
    }

    private static async Task<LogRecorder> RunWithEmbeddingSpaceAsync(
        string embeddingSpaceId, StubScheduler? scheduler = null)
    {
        var services = new ServiceCollection();
        services.AddScoped<IEmbeddingProvider>(_ => new StubEmbeddingProvider(embeddingSpaceId));

        return await StartAsync(services, scheduler ?? new StubScheduler(succeeds: true));
    }

    private static async Task<LogRecorder> RunWithIndexAsync(
        StubScheduler scheduler,
        int requiredSkills,
        int storedSkills,
        int storedOrphans = 0,
        bool readFails = false)
    {
        var skills = Enumerable.Range(0, requiredSkills)
            .Select(i => new SkillDescriptor("skill_" + i, "Desc", SkillCategory.System, [], [], [], null))
            .ToList();
        var registry = Substitute.For<ISkillRegistry>();
        registry.GetAllSkills().Returns(skills);

        var recipes = Substitute.For<IAgentRecipeRepository>();
        recipes.GetAllEnabledAsync(Arg.Any<CancellationToken>()).Returns(new List<AgentRecipe>());

        var stored = skills.Take(storedSkills).Select(s => s.Name)
            .Concat(Enumerable.Range(0, storedOrphans).Select(i => "orphan_" + i))
            .ToDictionary(name => (KnowledgeEntryKind.Skill, name), _ => new byte[] { 1 });
        var repository = Substitute.For<IKnowledgeIndexRepository>();
        if (readFails)
        {
            repository.GetAllHashesAsync(Arg.Any<CancellationToken>())
                .Returns<IReadOnlyDictionary<(KnowledgeEntryKind, string), byte[]>>(_ => throw new InvalidOperationException("db down"));
        }
        else
        {
            repository.GetAllHashesAsync(Arg.Any<CancellationToken>())
                .Returns((IReadOnlyDictionary<(KnowledgeEntryKind, string), byte[]>)stored);
        }

        var services = new ServiceCollection();
        services.AddScoped<IEmbeddingProvider>(_ =>
            new StubEmbeddingProvider(KnowledgeIndexConstants.LocalEmbeddingSpacePrefix + "multilingual-e5-small@384"));
        services.AddScoped<IKnowledgeIndexCoverageProbe>(_ => new KnowledgeIndexCoverageProbe(registry, recipes, repository));

        return await StartAsync(services, scheduler);
    }

    private static async Task<LogRecorder> StartAsync(ServiceCollection services, StubScheduler scheduler)
    {
        var recorder = new LogRecorder();
        var sut = new KnowledgeIndexStartupService(services.BuildServiceProvider(), scheduler, recorder);

        await sut.StartAsync(CancellationToken.None);

        return recorder;
    }
    private sealed class StubEmbeddingProvider : IEmbeddingProvider
    {
        public StubEmbeddingProvider(string embeddingSpaceId) => EmbeddingSpaceId = embeddingSpaceId;

        public string EmbeddingSpaceId { get; }

        public int Dimension => KnowledgeIndexConstants.EmbeddingDimension;

        public Task<float[]> EmbedAsync(string text, CancellationToken cancellationToken) =>
            Task.FromResult(Array.Empty<float>());

        public Task<float[][]> EmbedBatchAsync(IReadOnlyList<string> texts, CancellationToken cancellationToken) =>
            Task.FromResult(Array.Empty<float[]>());

        public Task<float[]> EmbedQueryAsync(string query, CancellationToken cancellationToken) =>
            Task.FromResult(Array.Empty<float>());
    }

    private sealed class StubScheduler : IKnowledgeIndexSyncScheduler
    {
        public const string FailureMessage = "embedding provider down";

        private static readonly DateTimeOffset RunEnd = new(2026, 9, 11, 8, 0, 0, TimeSpan.Zero);

        private readonly bool _succeeds;

        public StubScheduler(bool succeeds) => _succeeds = succeeds;

        public List<string> RunNowReasons { get; } = [];

        public List<string> Requests { get; } = [];

        public KnowledgeIndexSyncStatus Status { get; private set; } =
            new(false, false, null, null, null, null);

        public void Request(string reason) => Requests.Add(reason);

        public Task RunNowAsync(string reason, CancellationToken cancellationToken)
        {
            RunNowReasons.Add(reason);
            Status = _succeeds
                ? new KnowledgeIndexSyncStatus(false, false, RunEnd, null, reason, null)
                : new KnowledgeIndexSyncStatus(false, false, null, RunEnd, reason, FailureMessage);
            return Task.CompletedTask;
        }
    }

    private sealed class LogRecorder : ILogger<KnowledgeIndexStartupService>
    {
        public List<string> Warnings { get; } = [];

        public List<string> Infos { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            var message = formatter(state, exception);
            if (logLevel == LogLevel.Warning)
            {
                Warnings.Add(message);
            }
            else if (logLevel == LogLevel.Information)
            {
                Infos.Add(message);
            }
        }
    }
}
