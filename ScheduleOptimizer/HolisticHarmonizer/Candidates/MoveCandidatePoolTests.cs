// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Harmonizer.Bitmap;
using Klacks.ScheduleOptimizer.Harmonizer.Conductor;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Candidates;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Llm;
using Klacks.ScheduleOptimizer.HolisticHarmonizer.Validation;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.ScheduleOptimizer.HolisticHarmonizer.Candidates;

[TestFixture]
public class MoveCandidatePoolTests
{
    private static readonly DateOnly Day0 = new(2026, 1, 5);

    [Test]
    public void Generate_UnknownIntent_ReturnsEmpty()
    {
        var pool = new MoveCandidatePool(BuildValidator(), Array.Empty<IMoveCandidateGenerator>());
        var bitmap = BuildBitmap(rows: 2, days: 2);

        var result = pool.Generate(bitmap, "no_such_intent");

        result.ShouldBeEmpty();
    }

    [Test]
    public void Generate_DelegatesToMatchingGenerator()
    {
        var bitmap = BuildBitmap(rows: 2, days: 3);
        bitmap.SetCell(0, 0, Work(CellSymbol.Early));
        bitmap.SetCell(0, 2, Work(CellSymbol.Early));
        bitmap.SetCell(1, 1, Work(CellSymbol.Late));

        var pool = new MoveCandidatePool(
            BuildValidator(),
            new[] { (IMoveCandidateGenerator)new ConsolidateBlockCandidateGenerator() });

        var result = pool.Generate(bitmap, HolisticIntent.ConsolidateBlock);

        result.Count.ShouldBeGreaterThan(0);
        result[0].RowA.ShouldBe(0);
    }

    [Test]
    public void Generate_AppliesTopKCap()
    {
        // 1 fragmented row (r0), 5 partner rows that all work on the gap day → 5 raw candidates.
        var bitmap = BuildBitmap(rows: 6, days: 3);
        bitmap.SetCell(0, 0, Work(CellSymbol.Early));
        bitmap.SetCell(0, 2, Work(CellSymbol.Early));
        for (var r = 1; r < 6; r++)
        {
            bitmap.SetCell(r, 1, Work(CellSymbol.Late));
        }

        var pool = new MoveCandidatePool(
            BuildValidator(),
            new[] { (IMoveCandidateGenerator)new ConsolidateBlockCandidateGenerator() },
            topPerIntent: 2);

        var result = pool.Generate(bitmap, HolisticIntent.ConsolidateBlock);

        result.Count.ShouldBe(2);
    }

    [Test]
    public void Generate_DropsCandidatesThatFailHardValidator()
    {
        // Locked target cell → validator rejects with LockedCell. The fake generator emits the
        // candidate anyway; the pool must drop it before exposing.
        var bitmap = BuildBitmap(rows: 2, days: 2);
        bitmap.SetCell(0, 0, new Cell(CellSymbol.Early, Guid.NewGuid(), [Guid.NewGuid()], IsLocked: true));
        bitmap.SetCell(1, 0, Work(CellSymbol.Late));

        var fakeGenerator = new FakeGenerator(
            HolisticIntent.ConsolidateBlock,
            new MoveCandidate(0, 0, 1, 0, "fake", 1.0));

        var pool = new MoveCandidatePool(BuildValidator(), new[] { (IMoveCandidateGenerator)fakeGenerator });

        var result = pool.Generate(bitmap, HolisticIntent.ConsolidateBlock);

        result.ShouldBeEmpty();
    }

    [Test]
    public void Generate_DeduplicatesSymmetricCoordinatePairs()
    {
        var bitmap = BuildBitmap(rows: 2, days: 2);
        bitmap.SetCell(0, 0, Work(CellSymbol.Early));
        bitmap.SetCell(1, 0, Work(CellSymbol.Late));

        var fakeGenerator = new FakeGenerator(
            HolisticIntent.ConsolidateBlock,
            new MoveCandidate(0, 0, 1, 0, "first", 1.0),
            new MoveCandidate(1, 0, 0, 0, "duplicate-mirrored", 0.5));

        var pool = new MoveCandidatePool(BuildValidator(), new[] { (IMoveCandidateGenerator)fakeGenerator });

        var result = pool.Generate(bitmap, HolisticIntent.ConsolidateBlock);

        result.Count.ShouldBe(1);
        result[0].Hint.ShouldBe("first");
    }

    [Test]
    public void Generate_SortsByExpectedBenefitDescending()
    {
        var bitmap = BuildBitmap(rows: 4, days: 2);
        bitmap.SetCell(0, 0, Work(CellSymbol.Early));
        bitmap.SetCell(1, 0, Work(CellSymbol.Late));
        bitmap.SetCell(2, 0, Work(CellSymbol.Night));
        bitmap.SetCell(3, 0, Work(CellSymbol.Other));

        var fakeGenerator = new FakeGenerator(
            HolisticIntent.ConsolidateBlock,
            new MoveCandidate(0, 0, 1, 0, "low", 1.0),
            new MoveCandidate(0, 0, 2, 0, "high", 5.0),
            new MoveCandidate(0, 0, 3, 0, "mid", 3.0));

        var pool = new MoveCandidatePool(BuildValidator(), new[] { (IMoveCandidateGenerator)fakeGenerator });

        var result = pool.Generate(bitmap, HolisticIntent.ConsolidateBlock);

        result.Select(c => c.Hint).ShouldBe(new[] { "high", "mid", "low" });
    }

    private static PlanMutationValidator BuildValidator()
        => new(new DomainAwareReplaceValidator(null));

    private static Cell Work(CellSymbol symbol)
        => new(symbol, Guid.NewGuid(), new[] { Guid.NewGuid() }, IsLocked: false);

    private static HarmonyBitmap BuildBitmap(int rows, int days)
    {
        var agents = new List<BitmapAgent>(rows);
        for (var r = 0; r < rows; r++)
        {
            agents.Add(new BitmapAgent($"agent-{r}", $"Agent {r}", 100m, new HashSet<CellSymbol>()));
        }
        var input = new BitmapInput(agents, Day0, Day0.AddDays(days - 1), []);
        return BitmapBuilder.Build(input);
    }

    private sealed class FakeGenerator : IMoveCandidateGenerator
    {
        private readonly MoveCandidate[] _candidates;
        public FakeGenerator(string intent, params MoveCandidate[] candidates)
        {
            Intent = intent;
            _candidates = candidates;
        }
        public string Intent { get; }
        public IEnumerable<MoveCandidate> Generate(HarmonyBitmap bitmap) => _candidates;
    }
}
