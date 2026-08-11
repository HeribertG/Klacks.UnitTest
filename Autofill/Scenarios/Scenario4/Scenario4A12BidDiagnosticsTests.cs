// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Agent;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Conductor;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Controller;
using Klacks.ScheduleOptimizer.TokenEvolution.Initialization;
using Klacks.UnitTest.Autofill.Fixtures;
using Klacks.UnitTest.Autofill.Support;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Autofill.Scenarios.Scenario4;

/// <summary>
/// Measurement A12.0 of the E4–E7 plan: which of the two competing explanations for the missing rest
/// day after a continued carry-in package is the real one. The plan names them H1 ("nobody was
/// stage-1 clean, so the escalation of round 2 handed the slot to a rule breaker") and H2 ("a
/// stage-1-clean agent existed but bid below the acceptance threshold and fell out of both lists,
/// because the threshold is applied BEFORE the constraint stage"). Both fit the code and neither was
/// ever measured.
/// <para>
/// The auction exposes its vetoes only as a flat list without an agent id
/// (<c>AuctionResult.Vetos</c>), so the per-agent verdict cannot be read off a production run. This
/// fixture therefore replays the token accumulation of <c>SlotAuctioneer.Run</c> — same starting
/// tokens, same slot order, same occupancy skip — and drives it with the winners the production run
/// actually picked, then asks the two production checkers for their verdict on every agent. The
/// replay is only trustworthy while it agrees with the production run, which is why the set of agents
/// it finds stage-0 clean is asserted against the set of agents the recording bidding agent was
/// actually asked for a bid: the auction asks exactly the stage-0-clean agents.
/// </para>
/// <para>
/// Nothing here asserts a planning outcome. The verdict is printed, not pinned — it decides which of
/// the two designs A12-a1 and A12-a2 gets built, and that decision belongs to the reader of the
/// report, not to a red test.
/// </para>
/// </summary>
[TestFixture]
[Explicit("A12.0 diagnosis; reports the bid situation and asserts only its own replay. Select it by name.")]
[Category("Autofill")]
[Category("Scenario4")]
public class Scenario4A12BidDiagnosticsTests
{
    private const string ProbeLabel = "A12.0 probe, full rule base";

    /// <summary>Employee whose package the plan names as the A12 case: order 3, early, closed 3 March.</summary>
    private const string FocusAgentId = "MA-07";

    /// <summary>First day the probe reports; 1 March plus three days is 4 March.</summary>
    private const int FirstProbeDayOffset = 3;

    /// <summary>Number of consecutive days the probe reports, starting at the first probe day.</summary>
    private const int ProbeDayCount = 2;

    /// <summary>Days before the first probe day whose assignments are listed for the focus employee.</summary>
    private const int FocusHistoryDays = 8;

    private const string Stage0VetoedBucket = "stage-0 vetoed, never asked for a bid";

    private const string Round1Bucket = "round-1 candidate";

    private const string Round2Bucket = "round-2 candidate (stage-1 violated, bid at or above threshold)";

    private const string CleanBelowThresholdBucket = "stage-1 CLEAN but below the acceptance threshold";

    private const string ViolatingBelowThresholdBucket = "stage-1 violated and below the acceptance threshold";

    private const string HypothesisOne = "H1";

    private const string HypothesisTwo = "H2";

    private const string HypothesisNone = "neither H1 nor H2";

    private const string HypothesisUnclear = "unclear";

    private const string ScoreFormat = "0.0000";

    private const string RuleSeparator = " | ";

    private const string ListSeparator = ", ";

    private const string KeySeparator = "#";

    private const string None = "none";

    private const string Clean = "clean";

    private static readonly double AcceptanceThreshold = new SlotAuctioneer(
        new FuzzyBiddingAgent(),
        new Stage0HardConstraintChecker(),
        new Stage1SoftConstraintChecker()).AcceptanceThreshold;

    private AutofillScenarioDefinition _definition = null!;

    private AuctionProbeResult _probe = null!;

    private readonly List<string> _replayMismatches = [];

    private readonly List<SlotReplay> _probeSlots = [];

    private int _replayedSlotCount;

    [OneTimeSetUp]
    public void RunTheAuctionOnceAndReplayItsConstraintChecks()
    {
        _definition = Scenario4CarryInFixture.BuildMainRun();
        _probe = AuctionProbe.Run(
            _definition.Context,
            _definition.Config.RandomSeed,
            ProbeLabel,
            AuctionProbe.NoSuppressedRules);

        Replay();
    }

    /// <summary>
    /// Trust anchor of this fixture. The auction asks for a bid exactly when stage 0 passed
    /// (<c>SlotAuctioneer.cs:127-134</c>), so the set of agents the replay finds stage-0 clean must be
    /// the set of agents the recorder saw. A single deviation means the replayed token list drifted
    /// from the production one, and then every stage-1 verdict below is worthless.
    /// </summary>
    [Test]
    public void TheReplayReproducesTheStage0VerdictOfEveryAuctionedSlot()
    {
        TestContext.Out.WriteLine(
            "slots auctioned by the production run: "
            + _probe.Outcome.Results.Count.ToString(CultureInfo.InvariantCulture)
            + ", by the replay: " + _replayedSlotCount.ToString(CultureInfo.InvariantCulture)
            + ", recorded bid calls: " + _probe.Records.Count.ToString(CultureInfo.InvariantCulture)
            + ", deviations: " + _replayMismatches.Count.ToString(CultureInfo.InvariantCulture));

        foreach (var mismatch in _replayMismatches)
        {
            TestContext.Out.WriteLine(mismatch);
        }

        _replayMismatches.ShouldBeEmpty(
            "the replay must reproduce the production auction exactly; the set of stage-0-clean agents "
            + "is the one thing the production run reveals per agent, and a deviation there proves the "
            + "replayed token list is not the list the production checkers saw");
    }

    /// <summary>
    /// The report the plan asks for: for every slot of the two A12-critical days, every agent with its
    /// stage-0 verdict, its stage-1 verdict, its bid score and the candidate list it ended up in, plus
    /// the winner and the round the production auction recorded. From that the H1/H2 verdict follows
    /// mechanically, and it is printed rather than asserted.
    /// </summary>
    [Test]
    public void TheBidSituationOfTheA12CriticalDaysDecidesBetweenH1AndH2()
    {
        ReportHeader();
        ReportFocusHistory();

        var verdicts = new List<string>();
        foreach (var replay in _probeSlots)
        {
            ReportSlot(replay);
            verdicts.Add(VerdictOf(replay));
        }

        ReportEscalationsOfTheProbeDays();

        var decisive = _probeSlots
            .Select((replay, index) => (Replay: replay, Verdict: verdicts[index]))
            .Where(entry => entry.Replay.RestGapOfWinner is not null
                && entry.Replay.RestGapOfWinner < entry.Replay.MinRestDaysOfWinner)
            .ToList();

        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine("--- slots of the probe days whose winner lands below its own MinRestDays ---");
        if (decisive.Count == 0)
        {
            TestContext.Out.WriteLine(
                "none — no award on these two days shortens a rest gap, so the A12 problem of this run was "
                + "not created here");
        }

        foreach (var entry in decisive)
        {
            TestContext.Out.WriteLine(
                Describe(entry.Replay.Date)
                + RuleSeparator + entry.Replay.Slot.Name
                + RuleSeparator + "winner " + (entry.Replay.Result.WinnerAgentId ?? None)
                + RuleSeparator + "round " + entry.Replay.Result.Round.ToString(CultureInfo.InvariantCulture)
                + RuleSeparator + "rest days before this slot "
                + Describe(entry.Replay.RestGapOfWinner)
                + " of " + Describe(entry.Replay.MinRestDaysOfWinner)
                + RuleSeparator + entry.Verdict);
        }

        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine("A12.0 VERDICT: " + OverallVerdict(decisive.Select(entry => entry.Verdict).ToList()));

        _probeSlots.ShouldNotBeEmpty(
            "the two probe days must contain auctioned slots, otherwise the report proves nothing either way");
        _replayMismatches.ShouldBeEmpty(
            "a drifted replay would print stage-1 verdicts the production auction never computed");
    }

    /// <summary>
    /// Replays the token accumulation of <c>SlotAuctioneer.Run:41-100</c>: the same locked tokens, the
    /// same carry-in pre-pass, the same slot order and the same occupancy skip, with the winner of
    /// every slot taken from the production result instead of being selected again. Selecting the
    /// winner again would add a second replica (<c>SelectWinner:179-214</c>) without adding a fact —
    /// the production winner is the ground truth this diagnosis needs.
    /// </summary>
    private void Replay()
    {
        var context = _definition.Context;
        var stage0 = new Stage0HardConstraintChecker();
        var stage1 = new Stage1SoftConstraintChecker();

        var tokens = new List<CoreToken>(
            LockedTokenFactory.BuildLockedTokens(context.LockedWorks, context.SchedulingMaxConsecutiveDays));
        var seed = CarryInContinuationSeeder.Seed(context, tokens);

        var orderedSlots = context.Shifts
            .OrderBy(slot => slot.Date, StringComparer.Ordinal)
            .ThenBy(slot => slot.StartTime, StringComparer.Ordinal)
            .ThenBy(slot => slot.Id, StringComparer.Ordinal)
            .ToList();

        var resultBySlot = new Dictionary<string, AuctionResult>(StringComparer.Ordinal);
        foreach (var result in _probe.Outcome.Results)
        {
            resultBySlot[result.SlotId] = result;
        }

        var biddersBySlot = new Dictionary<string, HashSet<string>>(StringComparer.Ordinal);
        var scoreByAgentAndSlot = new Dictionary<string, double>(StringComparer.Ordinal);
        foreach (var record in _probe.Records)
        {
            if (!biddersBySlot.TryGetValue(record.SlotKey, out var bidders))
            {
                bidders = new HashSet<string>(StringComparer.Ordinal);
                biddersBySlot[record.SlotKey] = bidders;
            }

            bidders.Add(record.Agent.Id);
            scoreByAgentAndSlot[record.SlotKey + KeySeparator + record.Agent.Id] = record.Bid.Score;
        }

        var firstProbeDay = AutofillSpecConstants.PeriodFrom.AddDays(FirstProbeDayOffset);
        var lastProbeDay = firstProbeDay.AddDays(ProbeDayCount - 1);

        foreach (var slot in orderedSlots)
        {
            if (seed.Occupancy.IsSatisfied(slot))
            {
                continue;
            }

            _replayedSlotCount++;
            var slotKey = slot.Date + RecordedBid.SlotKeySeparator + slot.Id;
            if (!resultBySlot.TryGetValue(slotKey, out var result))
            {
                _replayMismatches.Add(
                    "replay auctioned " + slotKey + " but the production run produced no result for it");
                continue;
            }

            var verdicts = new List<AgentVerdict>(context.Agents.Count);
            var stage0Clean = new HashSet<string>(StringComparer.Ordinal);

            foreach (var agent in context.Agents)
            {
                var veto0 = stage0.Check(agent, slot, tokens, context);
                VetoVerdict? veto1 = null;
                double? score = null;

                if (veto0 is null)
                {
                    stage0Clean.Add(agent.Id);
                    veto1 = stage1.Check(agent, slot, tokens, context);
                    if (scoreByAgentAndSlot.TryGetValue(slotKey + KeySeparator + agent.Id, out var recorded))
                    {
                        score = recorded;
                    }
                }

                verdicts.Add(new AgentVerdict(agent.Id, veto0, veto1, score, BucketOf(veto0, veto1, score)));
            }

            var recordedBidders = biddersBySlot.GetValueOrDefault(slotKey)
                ?? new HashSet<string>(StringComparer.Ordinal);
            if (!stage0Clean.SetEquals(recordedBidders))
            {
                _replayMismatches.Add(
                    slotKey + ": replay says stage-0 clean = " + Describe(stage0Clean)
                    + ", the auction asked = " + Describe(recordedBidders));
            }

            if (DateOnly.TryParse(slot.Date, out var slotDate)
                && slotDate >= firstProbeDay
                && slotDate <= lastProbeDay)
            {
                _probeSlots.Add(BuildSlotReplay(slot, slotDate, result, verdicts, tokens));
            }

            if (result.WinnerAgentId is null)
            {
                continue;
            }

            var winnerAgent = context.Agents.FirstOrDefault(candidate => candidate.Id == result.WinnerAgentId);
            tokens.Add(BuildToken(slot, result.WinnerAgentId, winnerAgent));
        }

        if (_replayedSlotCount != _probe.Outcome.Results.Count)
        {
            _replayMismatches.Add(
                "the replay auctioned " + _replayedSlotCount.ToString(CultureInfo.InvariantCulture)
                + " slots, the production run "
                + _probe.Outcome.Results.Count.ToString(CultureInfo.InvariantCulture)
                + " — the occupancy skip of the carry-in pre-pass did not reproduce, so the per-agent "
                + "verdicts below belong to a different auction");
        }
    }

    private SlotReplay BuildSlotReplay(
        CoreShift slot,
        DateOnly slotDate,
        AuctionResult result,
        IReadOnlyList<AgentVerdict> verdicts,
        IReadOnlyList<CoreToken> tokensBefore)
    {
        var snapshot = tokensBefore.ToList();
        int? restGap = null;
        int? minRestDays = null;
        var winner = _definition.Context.Agents
            .FirstOrDefault(agent => agent.Id == result.WinnerAgentId);

        if (winner is not null)
        {
            minRestDays = winner.MinRestDays;
            var nearest = NearestOccupiedDateBefore(winner.Id, slotDate, snapshot);
            if (nearest is not null)
            {
                restGap = slotDate.DayNumber - nearest.Value.DayNumber - 1;
            }
        }

        var focusDays = snapshot
            .Where(token => token.AgentId == FocusAgentId
                && token.Date < slotDate
                && token.Date >= slotDate.AddDays(-FocusHistoryDays))
            .Select(token => token.Date)
            .Distinct()
            .OrderBy(date => date)
            .ToList();

        return new SlotReplay(slot, slotDate, result, verdicts, restGap, minRestDays, focusDays);
    }

    private void ReportHeader()
    {
        var firstProbeDay = AutofillSpecConstants.PeriodFrom.AddDays(FirstProbeDayOffset);
        TestContext.Out.WriteLine(
            "scenario 4a auction, probe days "
            + Describe(firstProbeDay) + " .. " + Describe(firstProbeDay.AddDays(ProbeDayCount - 1)));
        TestContext.Out.WriteLine(
            "acceptance threshold " + Format(AcceptanceThreshold)
            + ", MinRestDays of the fixture "
            + AutofillSpecConstants.MinRestDays.ToString(CultureInfo.InvariantCulture)
            + ", employees " + _definition.Context.Agents.Count.ToString(CultureInfo.InvariantCulture));
        TestContext.Out.WriteLine(
            "H1 = no stage-1-clean agent existed, round 2 handed the slot to a rule breaker; "
            + "H2 = a stage-1-clean agent existed but bid below the threshold and fell out of both lists; "
            + "H3 = the constructed continuation was invisible to the checkers.");
    }

    /// <summary>
    /// Refutes or confirms the third possibility before the other two are weighed: if the constructed
    /// continuation of the focus employee is missing from the replayed assignment list, then
    /// <c>CheckMinRestDays</c> never saw the package end and neither H1 nor H2 is the cause.
    /// <c>CarryInContinuationSeeder.TryAppend:166</c> appends every placed token to the very list the
    /// auction hands to its checkers, so the expected answer is "visible" — measured, not assumed.
    /// </summary>
    private void ReportFocusHistory()
    {
        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine(
            "--- H3 check: assignments of " + FocusAgentId
            + " the checkers could see when the probe days were auctioned ---");

        foreach (var replay in _probeSlots)
        {
            TestContext.Out.WriteLine(
                replay.Slot.Date + " " + replay.Slot.Name
                + RuleSeparator + "in the replayed assignment list: "
                + (replay.FocusHistory.Count == 0
                    ? None
                    : string.Join(ListSeparator, replay.FocusHistory.Select(Describe))));
        }

        var boundaryDays = _definition.Context.BoundaryLockedWorks
            .Where(work => work.AgentId == FocusAgentId)
            .Select(work => work.Date)
            .Concat(_definition.Context.BoundaryExistingWorkBlockers
                .Where(blocker => blocker.AgentId == FocusAgentId)
                .Select(blocker => blocker.Date))
            .Distinct()
            .OrderBy(date => date)
            .ToList();

        TestContext.Out.WriteLine(
            "previous-month works of " + FocusAgentId + ": "
            + (boundaryDays.Count == 0 ? None : string.Join(ListSeparator, boundaryDays.Select(Describe))));
    }

    private void ReportSlot(SlotReplay replay)
    {
        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine(
            "=== " + replay.Slot.Date + " " + replay.Slot.Name
            + " " + replay.Slot.StartTime + "-" + replay.Slot.EndTime
            + " -> winner " + (replay.Result.WinnerAgentId ?? None)
            + " (round " + replay.Result.Round.ToString(CultureInfo.InvariantCulture) + ") ===");
        TestContext.Out.WriteLine("agent | stage-0 veto | stage-1 veto | score | landed in");

        foreach (var verdict in replay.Verdicts)
        {
            TestContext.Out.WriteLine(
                verdict.AgentId
                + RuleSeparator + (verdict.Stage0?.RuleName ?? Clean)
                + RuleSeparator + (verdict.Stage0 is not null ? None : verdict.Stage1?.RuleName ?? Clean)
                + RuleSeparator + (verdict.Score is null ? None : Format(verdict.Score.Value))
                + RuleSeparator + verdict.Bucket);
        }

        TestContext.Out.WriteLine(
            "round-1 candidates " + Count(replay, Round1Bucket)
            + ", round-2 candidates " + Count(replay, Round2Bucket)
            + ", stage-1 clean but below threshold " + Count(replay, CleanBelowThresholdBucket)
            + ", stage-1 violated and below threshold " + Count(replay, ViolatingBelowThresholdBucket)
            + ", stage-0 vetoed " + Count(replay, Stage0VetoedBucket));

        if (replay.RestGapOfWinner is not null)
        {
            TestContext.Out.WriteLine(
                "winner rest days before this slot: " + Describe(replay.RestGapOfWinner)
                + ", required " + Describe(replay.MinRestDaysOfWinner));
        }
    }

    private void ReportEscalationsOfTheProbeDays()
    {
        var firstProbeDay = AutofillSpecConstants.PeriodFrom.AddDays(FirstProbeDayOffset);
        var lastProbeDay = firstProbeDay.AddDays(ProbeDayCount - 1);
        var entries = _probe.Outcome.Escalation.Entries
            .Where(entry => entry.Date >= firstProbeDay && entry.Date <= lastProbeDay)
            .ToList();

        TestContext.Out.WriteLine(string.Empty);
        TestContext.Out.WriteLine("--- escalation log of the probe days ---");
        if (entries.Count == 0)
        {
            TestContext.Out.WriteLine(None);
            return;
        }

        foreach (var entry in entries)
        {
            TestContext.Out.WriteLine(
                Describe(entry.Date) + RuleSeparator + entry.AgentId
                + RuleSeparator + entry.RuleName + RuleSeparator + entry.Hint);
        }
    }

    /// <summary>
    /// Turns one slot into the hypothesis it supports. A round-1 award supports neither hypothesis:
    /// the winner was stage-1 clean, so whatever A12 measures later was not decided by this award.
    /// </summary>
    private static string VerdictOf(SlotReplay replay)
    {
        if (replay.Result.WinnerAgentId is null)
        {
            return HypothesisNone + " (slot stayed unassigned, round 3)";
        }

        if (replay.Result.Round == 1)
        {
            return HypothesisNone + " (round-1 award to a stage-1-clean agent)";
        }

        var cleanCandidates = replay.Verdicts
            .Where(verdict => verdict.Stage0 is null && verdict.Stage1 is null)
            .ToList();

        if (cleanCandidates.Count == 0)
        {
            return HypothesisOne + " (not one agent was stage-1 clean, round 2 had to break a rule)";
        }

        if (cleanCandidates.All(verdict => verdict.Score is not null && verdict.Score < AcceptanceThreshold))
        {
            return HypothesisTwo
                + " (stage-1-clean agents existed — "
                + string.Join(ListSeparator, cleanCandidates.Select(verdict =>
                    verdict.AgentId + " " + Format(verdict.Score ?? 0)))
                + " — but every one of them bid below " + Format(AcceptanceThreshold) + ")";
        }

        return HypothesisUnclear
            + ", because a stage-1-clean agent at or above the threshold existed and the slot was still "
            + "awarded in round 2: "
            + string.Join(ListSeparator, cleanCandidates.Select(verdict =>
                verdict.AgentId + " " + (verdict.Score is null ? None : Format(verdict.Score.Value))));
    }

    private static string OverallVerdict(IReadOnlyList<string> decisiveVerdicts)
    {
        if (decisiveVerdicts.Count == 0)
        {
            return HypothesisNone
                + " — no award on the probe days shortens a rest gap. The A12 finding of this run does not "
                + "originate in the auction of these two days; look at the GA stage instead (plan section 4.4)";
        }

        var supportsOne = decisiveVerdicts.Any(verdict => verdict.StartsWith(HypothesisOne, StringComparison.Ordinal));
        var supportsTwo = decisiveVerdicts.Any(verdict => verdict.StartsWith(HypothesisTwo, StringComparison.Ordinal));

        if (supportsOne && !supportsTwo)
        {
            return HypothesisOne + " — build A12-a1, the severity ladder in round 2; A12-a2 is not needed";
        }

        if (supportsTwo && !supportsOne)
        {
            return HypothesisTwo
                + " — build A12-a2, the intermediate round for legal candidates below the threshold; "
                + "A12-a1 is not needed";
        }

        if (supportsOne && supportsTwo)
        {
            return HypothesisOne + " and " + HypothesisTwo
                + " both occur on different slots; the plan's assumption that only one design is needed "
                + "does not hold and the decision has to weigh how many slots each branch explains";
        }

        return HypothesisUnclear + ", because " + string.Join(ListSeparator, decisiveVerdicts);
    }

    private static string BucketOf(VetoVerdict? veto0, VetoVerdict? veto1, double? score)
    {
        if (veto0 is not null)
        {
            return Stage0VetoedBucket;
        }

        var aboveThreshold = score is not null && score >= AcceptanceThreshold;
        if (veto1 is not null)
        {
            return aboveThreshold ? Round2Bucket : ViolatingBelowThresholdBucket;
        }

        return aboveThreshold ? Round1Bucket : CleanBelowThresholdBucket;
    }

    /// <summary>
    /// Nearest earlier day the agent is occupied on, over the replayed assignments and the fixed works
    /// of the previous month. Replica of <c>Stage1SoftConstraintChecker.FindNearestOccupiedDate:223-237</c>
    /// widened by the boundary access of <c>IsOccupiedByBoundaryWork:155-174</c>, because the rest gap
    /// the report prints is the one A12 measures, not the one the soft checker currently sees.
    /// </summary>
    private DateOnly? NearestOccupiedDateBefore(string agentId, DateOnly anchor, IReadOnlyList<CoreToken> assigned)
    {
        DateOnly? best = null;

        foreach (var token in assigned)
        {
            if (token.AgentId != agentId)
            {
                continue;
            }

            Consider(token.Date, anchor, ref best);
            if (token.EndAt.Date > token.StartAt.Date)
            {
                Consider(DateOnly.FromDateTime(token.EndAt), anchor, ref best);
            }
        }

        foreach (var locked in _definition.Context.BoundaryLockedWorks)
        {
            if (locked.AgentId != agentId)
            {
                continue;
            }

            Consider(locked.Date, anchor, ref best);
            if (locked.EndAt.Date > locked.StartAt.Date)
            {
                Consider(DateOnly.FromDateTime(locked.EndAt), anchor, ref best);
            }
        }

        foreach (var blocker in _definition.Context.BoundaryExistingWorkBlockers)
        {
            if (blocker.AgentId != agentId)
            {
                continue;
            }

            Consider(blocker.Date, anchor, ref best);
            if (blocker.EndAt.Date > blocker.StartAt.Date)
            {
                Consider(DateOnly.FromDateTime(blocker.EndAt), anchor, ref best);
            }
        }

        return best;
    }

    private static void Consider(DateOnly candidate, DateOnly anchor, ref DateOnly? best)
    {
        if (candidate >= anchor)
        {
            return;
        }

        if (!best.HasValue || candidate > best.Value)
        {
            best = candidate;
        }
    }

    /// <summary>
    /// Replica of <c>SlotAuctioneer.BuildToken:216-240</c>. The replay must add exactly the token the
    /// production run added, or the very next stage-0 and stage-1 check sees a different world.
    /// </summary>
    private static CoreToken BuildToken(CoreShift slot, string agentId, CoreAgent? agent)
    {
        var date = DateOnly.Parse(slot.Date, CultureInfo.InvariantCulture);
        var start = TimeOnly.TryParse(slot.StartTime, out var parsedStart) ? parsedStart : new TimeOnly(8, 0);
        var end = TimeOnly.TryParse(slot.EndTime, out var parsedEnd) ? parsedEnd : start.AddHours(8);
        var shiftTypeIndex = ShiftTypeInference.FromSpan(start, end);
        var totalHours = (decimal)slot.Hours;

        return new CoreToken(
            WorkIds: [],
            ShiftTypeIndex: shiftTypeIndex,
            Date: date,
            TotalHours: totalHours,
            StartAt: date.ToDateTime(start),
            EndAt: end <= start ? date.AddDays(1).ToDateTime(end) : date.ToDateTime(end),
            BlockId: Guid.NewGuid(),
            PositionInBlock: 0,
            IsLocked: false,
            LocationContext: slot.LocationContext,
            ShiftRefId: Guid.TryParse(slot.Id, out var shiftRefId) ? shiftRefId : Guid.Empty,
            AgentId: agentId)
        {
            Surcharges = SurchargeEstimator.Estimate(totalHours, shiftTypeIndex, date, agent),
        };
    }

    private static string Count(SlotReplay replay, string bucket)
        => replay.Verdicts.Count(verdict => verdict.Bucket == bucket).ToString(CultureInfo.InvariantCulture);

    private static string Format(double value) => value.ToString(ScoreFormat, CultureInfo.InvariantCulture);

    private static string Describe(DateOnly date)
        => date.ToString(AutofillSpecConstants.IsoDateFormat, CultureInfo.InvariantCulture);

    private static string Describe(int? value)
        => value is null ? None : value.Value.ToString(CultureInfo.InvariantCulture);

    private static string Describe(IReadOnlyCollection<string> items)
        => items.Count == 0
            ? None
            : string.Join(ListSeparator, items.OrderBy(item => item, StringComparer.Ordinal));

    /// <param name="AgentId">Employee the verdict belongs to</param>
    /// <param name="Stage0">Hard veto, or null when the agent passed stage 0</param>
    /// <param name="Stage1">Soft veto, or null when the agent passed stage 1 or never reached it</param>
    /// <param name="Score">Bid score the production run recorded, or null when no bid was asked for</param>
    /// <param name="Bucket">Candidate list the agent ended up in</param>
    private sealed record AgentVerdict(
        string AgentId, VetoVerdict? Stage0, VetoVerdict? Stage1, double? Score, string Bucket);

    /// <param name="Slot">Slot of a probe day</param>
    /// <param name="Date">Calendar day of the slot</param>
    /// <param name="Result">What the production auction decided for this slot</param>
    /// <param name="Verdicts">Per-agent stage-0/stage-1 verdict at the moment of the award</param>
    /// <param name="RestGapOfWinner">Free days between the winner's previous work day and this slot</param>
    /// <param name="MinRestDaysOfWinner">Rest days the winner's contract asks for</param>
    /// <param name="FocusHistory">Days the focus employee already held when this slot was auctioned</param>
    private sealed record SlotReplay(
        CoreShift Slot,
        DateOnly Date,
        AuctionResult Result,
        IReadOnlyList<AgentVerdict> Verdicts,
        int? RestGapOfWinner,
        int? MinRestDaysOfWinner,
        IReadOnlyList<DateOnly> FocusHistory);
}
