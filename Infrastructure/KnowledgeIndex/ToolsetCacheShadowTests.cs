// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.KnowledgeIndex.Application.Services;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.KnowledgeIndex;

[TestFixture]
public class ToolsetCacheShadowTests
{
    // The existing cases are about keying and reuse, not about risk, so every skill they use counts
    // as read-only and their expectations stay unaffected by the writes= figures.
    private static readonly IReadOnlySet<string> ReadOnlyAll =
        new HashSet<string>(StringComparer.Ordinal) { "a", "b", "c" };

    private CapturingLogger _logger = null!;
    private ToolsetCacheShadow _shadow = null!;

    [SetUp]
    public void Setup()
    {
        _logger = new CapturingLogger();
        // Mirrors production, where MemoryCache is configured with a SizeLimit - entries written
        // without a Size throw there, so a cache without the limit would not catch that mistake.
        var cache = new MemoryCache(new MemoryCacheOptions { SizeLimit = 1000 });
        var configuration = new ConfigurationBuilder().AddInMemoryCollection().Build();

        _shadow = new ToolsetCacheShadow(cache, configuration, _logger);
    }

    [Test]
    public void ObserveAndRecord_FirstTimeForASignature_ReportsAMiss()
    {
        _shadow.ObserveAndRecord(["search_employees"], [], ["a", "b"], ReadOnlyAll);

        Line().ShouldContain("hit=0");
    }

    [Test]
    public void ObserveAndRecord_SameSignatureAndSameResult_ReportsAgreement()
    {
        _shadow.ObserveAndRecord(["search_employees"], [], ["a", "b"], ReadOnlyAll);
        _shadow.ObserveAndRecord(["search_employees"], [], ["b", "a"], ReadOnlyAll);

        var second = _logger.Messages[^1];
        second.ShouldContain("hit=1");
        second.ShouldContain("same=1");
    }

    // The number that decides whether this is safe to switch on. A hit that disagrees with the
    // pipeline is exactly the silent failure shadow mode exists to surface.
    [Test]
    public void ObserveAndRecord_SameSignatureButDifferentResult_ReportsDisagreementWithCounts()
    {
        _shadow.ObserveAndRecord(["search_employees"], [], ["a", "b"], ReadOnlyAll);
        _shadow.ObserveAndRecord(["search_employees"], [], ["b", "c"], ReadOnlyAll);

        var second = _logger.Messages[^1];
        second.ShouldContain("hit=1");
        second.ShouldContain("same=0");
        second.ShouldContain("missing=1");
        second.ShouldContain("extra=1");
    }

    // Different permissions mean a different permitted skill set, so the entries must not be shared.
    [Test]
    public void ObserveAndRecord_SameSignatureDifferentPermissions_DoesNotReuseTheEntry()
    {
        _shadow.ObserveAndRecord(["search_employees"], ["right-a"], ["a"], ReadOnlyAll);
        _shadow.ObserveAndRecord(["search_employees"], ["right-b"], ["a"], ReadOnlyAll);

        _logger.Messages[^1].ShouldContain("hit=0");
    }

    // A rebuilt index can change which keywords match at all, so a signature recorded before it may
    // no longer mean the same thing.
    [Test]
    public void ObserveAndRecord_AfterInvalidate_DoesNotReusePreviousEntries()
    {
        _shadow.ObserveAndRecord(["search_employees"], [], ["a"], ReadOnlyAll);
        _shadow.Invalidate();
        _shadow.ObserveAndRecord(["search_employees"], [], ["a"], ReadOnlyAll);

        _logger.Messages[^1].ShouldContain("hit=0");
    }

    // No keyword match means no action to key on; a real cache would not apply, so these turns are
    // counted separately rather than as misses.
    [Test]
    public void ObserveAndRecord_NoKeywordMatch_ReportsNotApplicable()
    {
        _shadow.ObserveAndRecord([], [], ["a"], ReadOnlyAll);

        Line().ShouldContain("applicable=0");
    }

    [Test]
    public void ObserveAndRecord_Disabled_WritesNothing()
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["KnowledgeIndex:ToolsetCacheShadowEnabled"] = "false"
            })
            .Build();
        var shadow = new ToolsetCacheShadow(
            new MemoryCache(new MemoryCacheOptions { SizeLimit = 1000 }), configuration, _logger);

        shadow.ObserveAndRecord(["search_employees"], [], ["a"], ReadOnlyAll);

        _logger.Messages.ShouldBeEmpty();
    }

    // The writes figure is what tells the evaluation whether a "read-only turns may use the cache"
    // rule would still cover enough traffic to be worth having.
    [Test]
    public void ObserveAndRecord_AllSkillsReadOnly_ReportsZeroWrites()
    {
        _shadow.ObserveAndRecord(["search_employees"], [], ["a", "b"], ReadOnlyAll);

        Line().ShouldContain("writes=0");
    }

    [Test]
    public void ObserveAndRecord_SomeSkillsWrite_CountsOnlyThose()
    {
        _shadow.ObserveAndRecord(["delete_client"], [], ["a", "delete_client", "update_client"], ReadOnlyAll);

        Line().ShouldContain("writes=2");
    }

    // Deliberately conservative: a skill disabled between writing and reading an entry must count as
    // the dangerous kind, never as harmless.
    [Test]
    public void ObserveAndRecord_SkillNotInTheReadOnlySet_CountsAsWriting()
    {
        _shadow.ObserveAndRecord(["search_employees"], [], ["unknown_skill"], ReadOnlyAll);

        Line().ShouldContain("writes=1");
    }

    [Test]
    public void ObserveAndRecord_OnAHit_ReportsWritesForBothSides()
    {
        _shadow.ObserveAndRecord(["delete_client"], [], ["a", "delete_client"], ReadOnlyAll);
        _shadow.ObserveAndRecord(["delete_client"], [], ["a", "b"], ReadOnlyAll);

        var second = _logger.Messages[^1];
        second.ShouldContain("hit=1");
        second.ShouldContain("writes=0");
        second.ShouldContain("cachedWrites=1");
    }

    private string Line() => _logger.Messages.Single(m => m.Contains("[cache-shadow]"));

    private sealed class CapturingLogger : ILogger<ToolsetCacheShadow>
    {
        public List<string> Messages { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => Messages.Add(formatter(state, exception));
    }
}
