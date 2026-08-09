// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Conductor;

namespace Klacks.UnitTest.Autofill.Support;

/// <param name="Label">Name of the rule-base variant this run used</param>
/// <param name="Outcome">Everything the auction produced, including the per-slot bid telemetry</param>
/// <param name="Records">Every bid call with the runtime state it was computed from</param>
public sealed record AuctionProbeResult(
    string Label,
    AuctionRunOutcome Outcome,
    IReadOnlyList<RecordedBid> Records)
{
    /// <summary>Winner per slot key, or absent when the slot stayed unassigned.</summary>
    public IReadOnlyDictionary<string, string> WinnerBySlot => Outcome.Results
        .Where(result => result.WinnerAgentId is not null)
        .ToDictionary(result => result.SlotId, result => result.WinnerAgentId!, StringComparer.Ordinal);
}
