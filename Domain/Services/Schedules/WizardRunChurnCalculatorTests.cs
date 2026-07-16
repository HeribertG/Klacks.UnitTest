// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Services.Schedules;

namespace Klacks.UnitTest.Domain.Services.Schedules;

[TestFixture]
public class WizardRunChurnCalculatorTests
{
    private static readonly Guid AgentA = Guid.NewGuid();
    private static readonly Guid AgentB = Guid.NewGuid();
    private static readonly Guid Shift = Guid.NewGuid();
    private static readonly DateOnly Day = new(2026, 4, 10);

    private static WizardRunProposalCell Cell(
        Guid agent, DateOnly date, bool isDeleted = false, Guid? shift = null, bool isOverlaid = false)
        => new(agent, date, shift ?? Shift, new TimeOnly(8, 0), new TimeOnly(16, 0), isDeleted, isOverlaid);

    private static HashSet<(Guid, DateOnly)> Events(params (Guid, DateOnly)[] cells) => new(cells);

    [Test]
    public void Compute_EmptyProposal_ReturnsAllZero()
    {
        var result = WizardRunChurnCalculator.Compute([], Events());

        result.ProposalCellCount.ShouldBe(0);
        result.CorrectionChurn.ShouldBe(0.0);
        result.EventChurn.ShouldBe(0.0);
    }

    [Test]
    public void Compute_UnchangedProposal_CorrectionChurnIsZero()
    {
        var result = WizardRunChurnCalculator.Compute([Cell(AgentA, Day)], Events());

        result.ProposalCellCount.ShouldBe(1);
        result.CorrectedCellCount.ShouldBe(0);
        result.CorrectionChurn.ShouldBe(0.0);
        result.EventChurn.ShouldBe(0.0);
    }

    [Test]
    public void Compute_SoftDeletedCellWithoutEvent_CountsAsCorrection()
    {
        var result = WizardRunChurnCalculator.Compute([Cell(AgentA, Day, isDeleted: true)], Events());

        result.CorrectionChurn.ShouldBe(1.0);
        result.EventChurn.ShouldBe(0.0);
        result.CorrectedCellCount.ShouldBe(1);
    }

    [Test]
    public void Compute_CorrectedCellExplainedByEvent_CountsAsEventNotCorrection()
    {
        var result = WizardRunChurnCalculator.Compute(
            [Cell(AgentA, Day, isDeleted: true)],
            Events((AgentA, Day)));

        result.CorrectionChurn.ShouldBe(0.0);
        result.EventChurn.ShouldBe(1.0);
        result.EventCellCount.ShouldBe(1);
    }

    [Test]
    public void Compute_MixedCells_SplitsCorrectionAndEventOverProposalTotal()
    {
        var cells = new List<WizardRunProposalCell>
        {
            Cell(AgentA, Day, shift: Guid.NewGuid()),                            // unchanged
            Cell(AgentA, Day, isDeleted: true, shift: Guid.NewGuid()),           // correction
            Cell(AgentB, Day, isDeleted: true, shift: Guid.NewGuid()),           // event-explained
            Cell(AgentA, Day.AddDays(1), isDeleted: true, shift: Guid.NewGuid()), // correction
        };

        var result = WizardRunChurnCalculator.Compute(cells, Events((AgentB, Day)));

        result.ProposalCellCount.ShouldBe(4);
        result.CorrectedCellCount.ShouldBe(2);
        result.EventCellCount.ShouldBe(1);
        result.CorrectionChurn.ShouldBe(0.5);
        result.EventChurn.ShouldBe(0.25);
    }

    [Test]
    public void Compute_OverlaidCellWithoutDelete_CountsAsCorrection()
    {
        var result = WizardRunChurnCalculator.Compute(
            [Cell(AgentA, Day, isDeleted: false, isOverlaid: true)],
            Events());

        result.CorrectionChurn.ShouldBe(1.0);
        result.EventChurn.ShouldBe(0.0);
        result.CorrectedCellCount.ShouldBe(1);
    }

    [Test]
    public void Compute_OverlaidCellExplainedByEvent_CountsAsEventNotCorrection()
    {
        var result = WizardRunChurnCalculator.Compute(
            [Cell(AgentA, Day, isDeleted: false, isOverlaid: true)],
            Events((AgentA, Day)));

        result.CorrectionChurn.ShouldBe(0.0);
        result.EventChurn.ShouldBe(1.0);
        result.EventCellCount.ShouldBe(1);
    }

    [Test]
    public void Compute_DeletedAndOverlaidSameCell_CountsOnce()
    {
        var cells = new List<WizardRunProposalCell>
        {
            Cell(AgentA, Day, isDeleted: true, isOverlaid: true),
        };

        var result = WizardRunChurnCalculator.Compute(cells, Events());

        result.ProposalCellCount.ShouldBe(1);
        result.CorrectedCellCount.ShouldBe(1);
        result.CorrectionChurn.ShouldBe(1.0);
    }

    [Test]
    public void Compute_SameCellKeyCleanAndOverlaidWork_AggregatesAsCorrected()
    {
        var shift = Guid.NewGuid();
        var cells = new List<WizardRunProposalCell>
        {
            Cell(AgentA, Day, isDeleted: false, shift: shift, isOverlaid: false),
            Cell(AgentA, Day, isDeleted: false, shift: shift, isOverlaid: true),
        };

        var result = WizardRunChurnCalculator.Compute(cells, Events());

        result.ProposalCellCount.ShouldBe(1);
        result.CorrectedCellCount.ShouldBe(1);
        result.CorrectionChurn.ShouldBe(1.0);
    }

    [Test]
    public void Compute_DuplicateCellKey_CountedOnce()
    {
        var cells = new List<WizardRunProposalCell>
        {
            Cell(AgentA, Day, isDeleted: true),
            Cell(AgentA, Day, isDeleted: true),
        };

        var result = WizardRunChurnCalculator.Compute(cells, Events());

        result.ProposalCellCount.ShouldBe(1);
        result.CorrectionChurn.ShouldBe(1.0);
    }
}
