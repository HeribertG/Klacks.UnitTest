// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// SkillRerankOutcomeReader turns a captured trajectory into one data point of the threshold tuner. Only turns that
/// wanted a change and ran to their end teach anything; rows without the rerank observation are skipped.
/// </summary>

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class SkillRerankOutcomeReaderTests
{
    private const string Observed =
        "[{\"name\":\"create_client\",\"rerankMargin\":0.021,\"reranked\":true,\"rerankExplored\":false}]";

    [Test]
    public void SuccessfulUncorrectedWrite_IsASuccess()
    {
        var outcome = SkillRerankOutcomeReader.Read(Trajectory());

        outcome.ShouldBe(new SkillRerankOutcome(0.021, true, true));
    }

    [Test]
    public void CorrectedTurn_IsAFailure()
    {
        var trajectory = Trajectory();
        trajectory.WasCorrected = true;

        SkillRerankOutcomeReader.Read(trajectory)!.Success.ShouldBeFalse();
    }

    [Test]
    public void FailedOrMissingExecution_IsAFailure()
    {
        var failed = Trajectory();
        failed.WasSuccessful = false;
        var notExecuted = Trajectory();
        notExecuted.WasExecuted = false;
        notExecuted.WasSuccessful = null;

        SkillRerankOutcomeReader.Read(failed)!.Success.ShouldBeFalse();
        SkillRerankOutcomeReader.Read(notExecuted)!.Success.ShouldBeFalse();
    }

    [Test]
    public void ReadOnlyOrInterruptedTurns_AreSkipped()
    {
        var readOnly = Trajectory();
        readOnly.HadMutationIntent = false;
        var interrupted = Trajectory();
        interrupted.WasInterrupted = true;

        SkillRerankOutcomeReader.Read(readOnly).ShouldBeNull();
        SkillRerankOutcomeReader.Read(interrupted).ShouldBeNull();
    }

    [TestCase("[]")]
    [TestCase("[{\"name\":\"create_client\"}]")]
    [TestCase("[{\"rerankMargin\":\"x\",\"reranked\":true}]")]
    [TestCase("not json")]
    [TestCase("")]
    public void RowsWithoutAnObservation_AreSkipped(string json)
    {
        var trajectory = Trajectory();
        trajectory.KnowledgeIndexCandidatesJson = json;

        SkillRerankOutcomeReader.Read(trajectory).ShouldBeNull();
    }

    private static SkillSelectionTrajectory Trajectory() => new()
    {
        HadMutationIntent = true,
        WasExecuted = true,
        WasSuccessful = true,
        KnowledgeIndexCandidatesJson = Observed
    };
}
