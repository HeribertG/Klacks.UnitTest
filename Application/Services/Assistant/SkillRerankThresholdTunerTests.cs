// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// SkillRerankThresholdTuner keeps one learned threshold per installation in the settings table: it restores it at
/// startup and sharpens it from the captured turns. It does nothing unless the skill rerank runs in Adaptive mode.
/// </summary>

using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.KnowledgeIndex.Application.Interfaces;
using Microsoft.Extensions.Logging;
using SettingsKeys = Klacks.Api.Application.Constants.Settings;

namespace Klacks.UnitTest.Application.Services.Assistant;

[TestFixture]
public class SkillRerankThresholdTunerTests
{
    private ISkillRerankThreshold _threshold = null!;
    private ISettingsRepository _settings = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ISkillSelectionTrajectoryRepository _trajectories = null!;
    private IAgentRepository _agents = null!;
    private SkillRerankThresholdTuner _tuner = null!;
    private readonly Guid _agentId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _threshold = Substitute.For<ISkillRerankThreshold>();
        _threshold.IsLearned.Returns(true);
        _threshold.Current.Returns(0.03);
        _settings = Substitute.For<ISettingsRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _trajectories = Substitute.For<ISkillSelectionTrajectoryRepository>();
        _agents = Substitute.For<IAgentRepository>();
        _agents.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(new Agent { Id = _agentId });
        _tuner = new SkillRerankThresholdTuner(
            _threshold, _settings, _unitOfWork, _trajectories, _agents,
            Substitute.For<ILogger<SkillRerankThresholdTuner>>());
    }

    [Test]
    public async Task Restore_AppliesTheStoredValue_InInvariantCulture()
    {
        _settings.GetSettingNoTracking(SettingsKeys.KLACKSY_SKILL_RERANK_MARGIN)
            .Returns(new Klacks.Api.Domain.Models.Settings.Settings { Value = "0.045" });

        await _tuner.RestoreAsync();

        _threshold.Received(1).Set(0.045);
    }

    [TestCase(null)]
    [TestCase("abc")]
    [TestCase("NaN")]
    public async Task Restore_IgnoresAMissingOrBrokenValue(string? stored)
    {
        _settings.GetSettingNoTracking(SettingsKeys.KLACKSY_SKILL_RERANK_MARGIN)
            .Returns(stored == null ? null : new Klacks.Api.Domain.Models.Settings.Settings { Value = stored });

        await _tuner.RestoreAsync();

        _threshold.DidNotReceiveWithAnyArgs().Set(default);
    }

    [Test]
    public async Task NotAdaptive_NeitherRestoresNorTunes()
    {
        _threshold.IsLearned.Returns(false);

        await _tuner.RestoreAsync();
        var tuned = await _tuner.TuneAsync();

        tuned.ShouldBeNull();
        await _settings.DidNotReceiveWithAnyArgs().GetSettingNoTracking(default!);
        await _trajectories.DidNotReceiveWithAnyArgs().GetRecentAsync(default, default, default);
    }

    [Test]
    public async Task EnoughTurns_TunesPersistsAndCommits()
    {
        // Reranking makes no difference anywhere, so the threshold steps down to save latency.
        _trajectories.GetRecentAsync(_agentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Turns(4000));

        var tuned = await _tuner.TuneAsync();

        tuned.ShouldNotBeNull();
        tuned!.Value.ShouldBe(0.02, tolerance: 1e-9);
        _threshold.Received(1).Set(0.02);
        await _settings.Received(1).UpsertSettingAsync(SettingsKeys.KLACKSY_SKILL_RERANK_MARGIN, "0.02");
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task TooFewTurns_ChangesNothing()
    {
        _trajectories.GetRecentAsync(_agentId, Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(Turns(50));

        var tuned = await _tuner.TuneAsync();

        tuned.ShouldBeNull();
        _threshold.DidNotReceiveWithAnyArgs().Set(default);
        await _settings.DidNotReceiveWithAnyArgs().UpsertSettingAsync(default!, default!);
    }

    [Test]
    public async Task TurnsOutsideTheWindow_DoNotCount()
    {
        var old = Turns(4000);
        old.ForEach(t => t.CreateTime = DateTime.UtcNow.AddDays(-90));
        _trajectories.GetRecentAsync(_agentId, Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns(old);

        (await _tuner.TuneAsync()).ShouldBeNull();
    }

    private static List<SkillSelectionTrajectory> Turns(int count) =>
        Enumerable.Range(0, count)
            .Select(i => new SkillSelectionTrajectory
            {
                HadMutationIntent = true,
                WasExecuted = true,
                WasSuccessful = true,
                CreateTime = DateTime.UtcNow.AddHours(-1),
                KnowledgeIndexCandidatesJson = string.Create(
                    System.Globalization.CultureInfo.InvariantCulture,
                    $"[{{\"name\":\"s\",\"rerankMargin\":{(i * 7 % 120) / 1000.0 + 0.0005},\"reranked\":{(i % 2 == 0 ? "true" : "false")}}}]")
            })
            .ToList();
}
