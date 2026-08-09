// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Agent;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// Bidding agent that hands every call to a real agent and keeps a copy of the call. It changes no
/// value the auction sees, so a run with this decorator produces exactly the plan the undecorated
/// auction produces; it only makes the runtime state visible, which is otherwise private to the
/// auction loop and reachable from no public surface.
/// </summary>
public sealed class RecordingBiddingAgent : IBiddingAgent
{
    private readonly IBiddingAgent _inner;

    private readonly List<RecordedBid> _records = [];

    /// <param name="inner">Agent whose behaviour is recorded and passed through unchanged</param>
    public RecordingBiddingAgent(IBiddingAgent inner) => _inner = inner;

    /// <summary>Every bid the auction asked for, in the order it asked.</summary>
    public IReadOnlyList<RecordedBid> Records => _records;

    /// <inheritdoc />
    public Bid Evaluate(CoreAgent agent, CoreShift slot, AgentRuntimeState state, CoreWizardContext context)
    {
        var bid = _inner.Evaluate(agent, slot, state, context);
        _records.Add(new RecordedBid(agent, slot, state, bid));
        return bid;
    }
}
