// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Globalization;
using Klacks.ScheduleOptimizer.Models;
using Klacks.ScheduleOptimizer.TokenEvolution.Auction.Agent;
using Klacks.UnitTest.Autofill.Fixtures;

namespace Klacks.UnitTest.Autofill.Support;

/// <summary>
/// One bid the auction asked for, together with the exact inputs it was computed from. The auction
/// only asks agents that passed stage 0, so the presence of an agent in a recording IS the proof
/// that no hard constraint vetoed it — <c>VetoVerdict</c> carries no agent id and can therefore not
/// deliver that proof from the other side.
/// </summary>
/// <param name="Agent">Agent that submitted the bid</param>
/// <param name="Slot">Slot the bid was submitted for</param>
/// <param name="State">Runtime state as it stood when the bid was computed</param>
/// <param name="Bid">Bid the engine produced</param>
public sealed record RecordedBid(CoreAgent Agent, CoreShift Slot, AgentRuntimeState State, Bid Bid)
{
    /// <summary>Calendar day of the slot.</summary>
    public DateOnly Date => DateOnly.ParseExact(
        Slot.Date, AutofillSpecConstants.IsoDateFormat, CultureInfo.InvariantCulture);

    /// <summary>Slot key the auction uses in its results, so a bid can be joined to its winner.</summary>
    public string SlotKey => Slot.Date + SlotKeySeparator + Slot.Id;

    /// <summary>Separator <c>SlotAuctioneer</c> builds its result key with.</summary>
    public const string SlotKeySeparator = "/";
}
