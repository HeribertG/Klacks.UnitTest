// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for SkillCatalogRefresher — cache/registry/index refresh order and sync-failure isolation.
/// </summary>
namespace Klacks.UnitTest.Application.Services.Assistant;

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Microsoft.Extensions.Logging;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillCatalogRefresherTests
{
    private ISkillCacheService _cache = null!;
    private SkillRegistryInitializer _initializer = null!;
    private IKnowledgeIndexSynchronizer _knowledgeSync = null!;
    private SkillCatalogRefresher _refresher = null!;

    [SetUp]
    public void SetUp()
    {
        _cache = Substitute.For<ISkillCacheService>();
        _initializer = Substitute.For<SkillRegistryInitializer>(
            Substitute.For<IAgentSkillRepository>(),
            Substitute.For<ISkillRegistry>(),
            Substitute.For<ILogger<SkillRegistryInitializer>>());
        _knowledgeSync = Substitute.For<IKnowledgeIndexSynchronizer>();

        _refresher = new SkillCatalogRefresher(
            _cache, _initializer, _knowledgeSync, Substitute.For<ILogger<SkillCatalogRefresher>>());
    }

    [Test]
    public async Task RefreshAsync_RefreshesCacheRegistryAndIndexInOrder()
    {
        await _refresher.RefreshAsync("creating skill 'x'", CancellationToken.None);

        Received.InOrder(() =>
        {
            _cache.InvalidateCache();
            _initializer.InitializeAsync(Arg.Any<CancellationToken>());
            _knowledgeSync.SyncAsync(Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task RefreshAsync_IndexSyncFailure_DoesNotThrow()
    {
        _knowledgeSync.SyncAsync(Arg.Any<CancellationToken>())
            .Returns<Task>(_ => throw new InvalidOperationException("embedding provider down"));

        await Should.NotThrowAsync(() => _refresher.RefreshAsync("updating skill 'x'", CancellationToken.None));

        _cache.Received(1).InvalidateCache();
        await _initializer.Received(1).InitializeAsync(Arg.Any<CancellationToken>());
    }
}
