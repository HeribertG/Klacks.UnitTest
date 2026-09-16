// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// The learning loop must be able to tell "the user corrected us and we re-routed" from "the user
/// corrected us and nothing happened": the first is evidence about the ROUTING, the second about the
/// skill description. Both are implicit corrections, so without the distinction they land in the same
/// bucket and the sharpening loop draws the wrong conclusion from a turn that already recovered.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Assistant.Evaluation;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation;

[TestFixture]
public class TrajectoryCaptureCorrectionTypeTests
{
    private readonly Guid _agentId = Guid.NewGuid();

    private ISkillSelectionTrajectoryRepository _repository = null!;
    private SkillSelectionTrajectory _previous = null!;
    private TrajectoryCaptureService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _previous = new SkillSelectionTrajectory
        {
            Id = Guid.NewGuid(),
            AgentId = _agentId,
            UserId = "user-1",
            Locale = "de",
            UserMessageHash = "hash",
            IntentExcerpt = "Trag alle Mitarbeitenden in die Gruppe ein.",
            LlmChosenSkill = "find_customer_candidates",
            CreateTime = DateTime.UtcNow
        };

        _repository = Substitute.For<ISkillSelectionTrajectoryRepository>();
        _repository.FindMostRecentByAgentAndUserAsync(_agentId, "user-1").Returns(_previous);

        var phraseRepository = Substitute.For<ISkillPhraseRepository>();
        phraseRepository.GetActiveBySourceAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new List<SkillPhrase>());

        _service = new TrajectoryCaptureService(
            _repository,
            Substitute.For<ISkillLearningCaseCollector>(),
            phraseRepository,
            Substitute.For<ISkillUsageRepository>(),
            Substitute.For<ILLMRepository>(),
            NullLogger<TrajectoryCaptureService>.Instance);
    }

    private LLMContext Context(bool rerouted) => new()
    {
        Message = "Nein, ich meinte alle Mitarbeitenden in die Gruppe.",
        UserId = "user-1",
        GracefulCorrectionApplied = rerouted
    };

    [Test]
    public async Task CorrectionWithoutReRouting_StaysImplicit()
    {
        await _service.CaptureAsync(_agentId, Context(rerouted: false), "…", new List<LLMFunctionCall>());

        _previous.CorrectionType.ShouldBe(CorrectionTypes.Implicit);
        _previous.WasCorrected.ShouldBeTrue();
    }

    [Test]
    public async Task CorrectionWithReRouting_IsBookedAsGracefulRerouted()
    {
        await _service.CaptureAsync(_agentId, Context(rerouted: true), "…", new List<LLMFunctionCall>());

        _previous.CorrectionType.ShouldBe(CorrectionTypes.GracefulRerouted);
        _previous.WasCorrected.ShouldBeTrue();
    }
}
