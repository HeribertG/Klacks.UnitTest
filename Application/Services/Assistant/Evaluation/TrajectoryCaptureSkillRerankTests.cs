// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The skill rerank decision of a turn is written into its candidate JSON, under the keys the threshold tuner reads
/// back (SkillRerankJsonKeys). Without a decision the entries keep their old shape.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Services.Assistant.Evaluation;
using Klacks.Api.Domain.Constants;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Services.Assistant.Evaluation;

[TestFixture]
public class TrajectoryCaptureSkillRerankTests
{
    private ISkillSelectionTrajectoryRepository _repository = null!;
    private TrajectoryCaptureService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<ISkillSelectionTrajectoryRepository>();
        var phrases = Substitute.For<ISkillPhraseRepository>();
        phrases.GetActiveBySourceAsync(Arg.Any<string>(), Arg.Any<int>(), Arg.Any<CancellationToken>()).Returns([]);
        var usage = Substitute.For<ISkillUsageRepository>();
        usage.GetByTurnIdAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns([]);
        _service = new TrajectoryCaptureService(
            _repository, Substitute.For<ISkillLearningCaseCollector>(), phrases, usage,
            Substitute.For<ILLMRepository>(), Substitute.For<ILogger<TrajectoryCaptureService>>());
    }

    [Test]
    public async Task TheDecision_IsWrittenIntoEveryCandidate()
    {
        var json = await CaptureAsync(new SkillRerankObservation(0.012, true, true));

        using var document = JsonDocument.Parse(json);
        foreach (var entry in document.RootElement.EnumerateArray())
        {
            entry.GetProperty(SkillRerankJsonKeys.Margin).GetDouble().ShouldBe(0.012, tolerance: 1e-9);
            entry.GetProperty(SkillRerankJsonKeys.Reranked).GetBoolean().ShouldBeTrue();
            entry.GetProperty(SkillRerankJsonKeys.Explored).GetBoolean().ShouldBeTrue();
        }

        document.RootElement.GetArrayLength().ShouldBe(2);
    }

    [Test]
    public async Task WithoutADecision_TheEntriesKeepTheirShape()
    {
        var json = await CaptureAsync(null);

        using var document = JsonDocument.Parse(json);
        document.RootElement[0].TryGetProperty(SkillRerankJsonKeys.Margin, out _).ShouldBeFalse();
    }

    [Test]
    public async Task TheCapturedRow_IsReadableByTheTuner()
    {
        SkillSelectionTrajectory? captured = null;
        await _repository.AddAsync(Arg.Do<SkillSelectionTrajectory>(r => captured = r));

        await CaptureAsync(new SkillRerankObservation(0.012, false, false));

        captured!.HadMutationIntent = true;
        SkillRerankOutcomeReader.Read(captured)!.ShouldBe(new SkillRerankOutcome(0.012, false, false));
    }

    private async Task<string> CaptureAsync(SkillRerankObservation? observation)
    {
        SkillSelectionTrajectory? captured = null;
        _repository.When(r => r.AddAsync(Arg.Any<SkillSelectionTrajectory>(), Arg.Any<CancellationToken>()))
            .Do(call => captured = call.Arg<SkillSelectionTrajectory>());

        var context = new LLMContext
        {
            Message = "Zeig mir die Kunden",
            UserId = "user-1",
            AvailableFunctions = [new LLMFunction { Name = "list_clients" }, new LLMFunction { Name = "get_client" }],
            SkillRerank = observation
        };

        await _service.CaptureAsync(Guid.NewGuid(), context, "Bitte.", []);

        return captured!.KnowledgeIndexCandidatesJson;
    }
}
