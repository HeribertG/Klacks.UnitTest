// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// ExplainDepthResolver: a persisted per-user override wins over the usage heuristic, the heuristic promotes a user to
/// advanced only past its thresholds, everything else is beginner; results are cached per user, Invalidate drops the
/// cache entry, and a failing read falls back to beginner without being cached. An advanced estimate lives longer in
/// the cache than a beginner estimate or an override, which can still change at any moment.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class ExplainDepthResolverTests
{
    private static readonly DateTime FirstWork = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

    private IAgentMemoryRepository _memoryRepository = null!;
    private IUserExperienceSignalReader _signalReader = null!;
    private MemoryCache _cache = null!;
    private ExplainDepthResolver _sut = null!;
    private Guid _userId;

    [SetUp]
    public void SetUp()
    {
        _userId = Guid.NewGuid();
        _memoryRepository = Substitute.For<IAgentMemoryRepository>();
        _signalReader = Substitute.For<IUserExperienceSignalReader>();
        _cache = new MemoryCache(new MemoryCacheOptions());
        _memoryRepository.GetByUserAndKeyAsync(Arg.Any<Guid>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns((AgentMemory?)null);
        _signalReader.ReadAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(UserExperienceSignals.None);
        _sut = new ExplainDepthResolver(
            _memoryRepository, _signalReader, _cache, Substitute.For<ILogger<ExplainDepthResolver>>());
    }

    [TearDown]
    public void TearDown() => _cache.Dispose();

    private void SetOverride(string value) =>
        _memoryRepository.GetByUserAndKeyAsync(_userId, ExplainDepthDefaults.OverrideMemoryKey, Arg.Any<CancellationToken>())
            .Returns(new AgentMemory { UserId = _userId, Key = ExplainDepthDefaults.OverrideMemoryKey, Content = value });

    private void SetSignals(int works, int spanDays, int skillWrites) =>
        _signalReader.ReadAsync(_userId, Arg.Any<CancellationToken>())
            .Returns(new UserExperienceSignals(
                works,
                works > 0 ? FirstWork : null,
                works > 0 ? FirstWork.AddDays(spanDays) : null,
                skillWrites));

    [Test]
    public async Task AdvancedOverride_WinsOverABeginnerHeuristic_WithoutReadingSignals()
    {
        SetOverride(ExplainDepthDefaults.OverrideValueAdvanced);

        var profile = await _sut.ResolveAsync(_userId.ToString());

        profile.ShouldBe(new ExplainDepthProfile(ExplainExperienceLevel.Advanced, true));
        await _signalReader.DidNotReceiveWithAnyArgs().ReadAsync(default, default);
    }

    [Test]
    public async Task BeginnerOverride_WinsOverAnAdvancedHeuristic()
    {
        SetOverride(" Beginner ");
        SetSignals(500, 365, 100);

        var profile = await _sut.ResolveAsync(_userId.ToString());

        profile.ShouldBe(new ExplainDepthProfile(ExplainExperienceLevel.Beginner, true));
    }

    [Test]
    public async Task AutoOverride_FallsBackToTheHeuristic()
    {
        SetOverride(ExplainDepthDefaults.OverrideValueAuto);
        SetSignals(ExplainDepthDefaults.MinOwnWorks, ExplainDepthDefaults.MinActiveSpanDays, 0);

        var profile = await _sut.ResolveAsync(_userId.ToString());

        profile.ShouldBe(new ExplainDepthProfile(ExplainExperienceLevel.Advanced, false));
    }

    [Test]
    public async Task WorksAndSpanAtThreshold_IsAdvanced()
    {
        SetSignals(ExplainDepthDefaults.MinOwnWorks, ExplainDepthDefaults.MinActiveSpanDays, 0);

        var profile = await _sut.ResolveAsync(_userId.ToString());

        profile!.Level.ShouldBe(ExplainExperienceLevel.Advanced);
        profile.IsExplicitOverride.ShouldBeFalse();
    }

    [Test]
    public async Task EnoughWorksButTooShortSpan_IsBeginner()
    {
        SetSignals(ExplainDepthDefaults.MinOwnWorks * 5, ExplainDepthDefaults.MinActiveSpanDays - 1, 0);

        (await _sut.ResolveAsync(_userId.ToString()))!.Level.ShouldBe(ExplainExperienceLevel.Beginner);
    }

    [Test]
    public async Task LongSpanButTooFewWorks_IsBeginner()
    {
        SetSignals(ExplainDepthDefaults.MinOwnWorks - 1, ExplainDepthDefaults.MinActiveSpanDays * 3, 0);

        (await _sut.ResolveAsync(_userId.ToString()))!.Level.ShouldBe(ExplainExperienceLevel.Beginner);
    }

    [Test]
    public async Task SuccessfulSkillWritesAtThreshold_IsAdvancedOnTheirOwn()
    {
        SetSignals(0, 0, ExplainDepthDefaults.MinSuccessfulSkillWrites);

        (await _sut.ResolveAsync(_userId.ToString()))!.Level.ShouldBe(ExplainExperienceLevel.Advanced);
    }

    [Test]
    public async Task SuccessfulSkillWritesBelowThreshold_IsBeginner()
    {
        SetSignals(0, 0, ExplainDepthDefaults.MinSuccessfulSkillWrites - 1);

        (await _sut.ResolveAsync(_userId.ToString()))!.Level.ShouldBe(ExplainExperienceLevel.Beginner);
    }

    [Test]
    public async Task NoOverrideAndNoSignals_IsBeginner()
    {
        var profile = await _sut.ResolveAsync(_userId.ToString());

        profile.ShouldBe(new ExplainDepthProfile(ExplainExperienceLevel.Beginner, false));
    }

    [Test]
    public async Task SecondResolve_ComesFromTheCache()
    {
        await _sut.ResolveAsync(_userId.ToString());
        await _sut.ResolveAsync(_userId.ToString());

        await _memoryRepository.Received(1).GetByUserAndKeyAsync(_userId, ExplainDepthDefaults.OverrideMemoryKey, Arg.Any<CancellationToken>());
        await _signalReader.Received(1).ReadAsync(_userId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Invalidate_ForcesAFreshResolve()
    {
        await _sut.ResolveAsync(_userId.ToString());
        SetOverride(ExplainDepthDefaults.OverrideValueAdvanced);

        _sut.Invalidate(_userId);
        var profile = await _sut.ResolveAsync(_userId.ToString());

        profile.ShouldBe(new ExplainDepthProfile(ExplainExperienceLevel.Advanced, true));
    }

    [TestCase(null)]
    [TestCase("")]
    [TestCase("not-a-guid")]
    [TestCase("00000000-0000-0000-0000-000000000000")]
    public async Task UnresolvableUser_ReturnsNull_WithoutReading(string? userId)
    {
        var profile = await _sut.ResolveAsync(userId);

        profile.ShouldBeNull();
        await _memoryRepository.DidNotReceiveWithAnyArgs().GetByUserAndKeyAsync(default, default!, default);
    }

    [Test]
    public async Task FailingRead_FallsBackToBeginner_AndIsNotCached()
    {
        _signalReader.ReadAsync(_userId, Arg.Any<CancellationToken>())
            .Returns<Task<UserExperienceSignals>>(_ => throw new InvalidOperationException("db down"));

        var first = await _sut.ResolveAsync(_userId.ToString());
        SetSignals(0, 0, ExplainDepthDefaults.MinSuccessfulSkillWrites);
        var second = await _sut.ResolveAsync(_userId.ToString());

        first.ShouldBe(new ExplainDepthProfile(ExplainExperienceLevel.Beginner, false));
        second!.Level.ShouldBe(ExplainExperienceLevel.Advanced);
    }

    private async Task<TimeSpan?> ResolveAndCaptureCacheLifetime()
    {
        var entry = Substitute.For<ICacheEntry>();
        var cache = Substitute.For<IMemoryCache>();
        cache.CreateEntry(Arg.Any<object>()).Returns(entry);
        var sut = new ExplainDepthResolver(
            _memoryRepository, _signalReader, cache, Substitute.For<ILogger<ExplainDepthResolver>>());

        await sut.ResolveAsync(_userId.ToString());

        return entry.AbsoluteExpirationRelativeToNow;
    }

    [Test]
    public async Task AdvancedFromTheHeuristic_IsCachedForTheLongLifetime()
    {
        SetSignals(0, 0, ExplainDepthDefaults.MinSuccessfulSkillWrites);

        (await ResolveAndCaptureCacheLifetime()).ShouldBe(TimeSpan.FromHours(ExplainDepthDefaults.AdvancedEstimateCacheHours));
    }

    [Test]
    public async Task BeginnerFromTheHeuristic_IsCachedForTheShortLifetime()
    {
        (await ResolveAndCaptureCacheLifetime()).ShouldBe(TimeSpan.FromMinutes(ExplainDepthDefaults.CacheMinutes));
    }

    [TestCase(ExplainDepthDefaults.OverrideValueAdvanced)]
    [TestCase(ExplainDepthDefaults.OverrideValueBeginner)]
    public async Task Override_IsCachedForTheShortLifetime(string value)
    {
        SetOverride(value);

        (await ResolveAndCaptureCacheLifetime()).ShouldBe(TimeSpan.FromMinutes(ExplainDepthDefaults.CacheMinutes));
    }

    [Test]
    public async Task Invalidate_AlsoDropsALongLivedAdvancedEstimate()
    {
        SetSignals(0, 0, ExplainDepthDefaults.MinSuccessfulSkillWrites);
        await _sut.ResolveAsync(_userId.ToString());
        SetOverride(ExplainDepthDefaults.OverrideValueBeginner);

        _sut.Invalidate(_userId);
        var profile = await _sut.ResolveAsync(_userId.ToString());

        profile.ShouldBe(new ExplainDepthProfile(ExplainExperienceLevel.Beginner, true));
    }
}
