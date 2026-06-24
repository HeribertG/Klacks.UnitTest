// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text;
using Klacks.ScheduleRecovery.Model;

namespace Klacks.UnitTest.ScheduleRecovery;

/// <summary>
/// Deterministic helpers for the recovery golden-master tests: stable Guids whose .NET comparison order
/// is the trailing-byte order (so "agent 1 &lt; agent 2"), a fluent snapshot builder, and a canonical
/// proposal formatter used to assert exact deltas and run-to-run determinism (Token/JSON style).
/// </summary>
internal static class RecoveryTestKit
{
    /// <summary>A stable agent id ordered by <paramref name="n"/> under Guid.CompareTo.</summary>
    public static Guid Agent(int n) => new($"00000000-0000-0000-0000-0000000000{n:x2}");

    /// <summary>A stable shift id in a separate namespace from agent ids, ordered by <paramref name="n"/>.</summary>
    public static Guid Shift(int n) => new($"00000000-0000-0000-0000-0000000001{n:x2}");

    /// <summary>A stable group id in a separate namespace, ordered by <paramref name="n"/>.</summary>
    public static Guid Group(int n) => new($"00000000-0000-0000-0000-0000000002{n:x2}");

    public static DateOnly Day(int month, int day) => new(2026, month, day);

    public static DateOnly MondayOf(DateOnly date)
        => date.AddDays(-(((int)date.DayOfWeek + 6) % 7));

    public static DateTime At(DateOnly date, int hour, int minute = 0)
        => date.ToDateTime(new TimeOnly(hour, minute));

    /// <summary>Canonical, order-preserving rendering of a proposal used for exact-delta assertions.</summary>
    public static string Format(RecoveryProposal proposal)
    {
        var sb = new StringBuilder();
        foreach (var d in proposal.Deltas)
        {
            sb.AppendLine(
                $"D|{d.ShiftId}|{d.Date:yyyy-MM-dd}|{d.FromAgentId}|{d.ToAgentId}|{d.Tier}|{d.Hours}|{d.StartAt:O}|{d.EndAt:O}");
        }
        foreach (var u in proposal.Uncovered)
        {
            sb.AppendLine($"U|{u.ShiftId}|{u.Date:yyyy-MM-dd}|{u.Reason}|{u.IsCritical}");
        }
        sb.AppendLine(
            $"O|{proposal.Objective.UncoveredCritical}|{proposal.Objective.NewHardViolations}" +
            $"|{proposal.Objective.Perturbation}|{proposal.Objective.WorstOffPerturbation}");
        sb.AppendLine($"T|{proposal.HighestTier}");
        return sb.ToString();
    }
}

/// <summary>
/// Fluent in-memory builder for a <see cref="RecoverySnapshot"/>. Every collection is supplied explicitly,
/// so a test reads as a plain statement of the plan, the contract constraints, the availability gates and
/// the slot criticality.
/// </summary>
internal sealed class SnapshotBuilder
{
    private readonly List<DateOnly> _days = [];
    private readonly List<RecoveryAgent> _agents = [];
    private readonly Dictionary<CellKey, List<RecoveryWork>> _works = [];
    private readonly Dictionary<CellKey, DayAvailability> _availability = [];
    private readonly HashSet<IneligibleKey> _ineligible = [];
    private readonly HashSet<CellKey> _nonCritical = [];

    public SnapshotBuilder Days(params DateOnly[] days)
    {
        _days.AddRange(days);
        return this;
    }

    private Guid _receivingGroupId;

    public SnapshotBuilder Agent(
        Guid id,
        string name = "agent",
        decimal maxWeeklyHours = 0m,
        int maxConsecutiveDays = 0,
        decimal minPauseHours = 0m,
        decimal targetHoursDeficit = 0m,
        IEnumerable<Guid>? preferredShiftIds = null,
        IEnumerable<Guid>? blacklistedShiftIds = null,
        bool isInGroup = true,
        decimal maxDailyHours = 0m,
        bool performsShiftWork = true)
    {
        _agents.Add(new RecoveryAgent(
            id,
            name,
            maxWeeklyHours,
            maxConsecutiveDays,
            minPauseHours,
            targetHoursDeficit,
            preferredShiftIds?.ToHashSet() ?? [],
            blacklistedShiftIds?.ToHashSet() ?? [],
            isInGroup,
            maxDailyHours,
            performsShiftWork));
        return this;
    }

    public SnapshotBuilder ReceivingGroup(Guid groupId)
    {
        _receivingGroupId = groupId;
        return this;
    }

    public SnapshotBuilder Work(
        Guid agentId,
        DateOnly date,
        Guid shiftId,
        ShiftCategory category,
        DateTime startAt,
        DateTime endAt,
        decimal hours,
        bool locked = false,
        params Guid[] workIds)
    {
        Add(agentId, date, new RecoveryWork(
            category, shiftId, locked, startAt, endAt, hours, workIds.Length > 0 ? workIds : null));
        return this;
    }

    public SnapshotBuilder Break(Guid agentId, DateOnly date)
    {
        Add(agentId, date, new RecoveryWork(ShiftCategory.Break, null, true));
        return this;
    }

    private void Add(Guid agentId, DateOnly date, RecoveryWork work)
    {
        var key = new CellKey(agentId, date);
        if (!_works.TryGetValue(key, out var list))
        {
            list = [];
            _works[key] = list;
        }
        list.Add(work);
    }

    public SnapshotBuilder Availability(Guid agentId, DateOnly date, DayAvailability availability)
    {
        _availability[new CellKey(agentId, date)] = availability;
        return this;
    }

    public SnapshotBuilder Unavailable(Guid agentId, DateOnly date)
        => Availability(agentId, date, new DayAvailability(false, false, false));

    public SnapshotBuilder Ineligible(Guid agentId, Guid shiftId, DateOnly date)
    {
        _ineligible.Add(new IneligibleKey(agentId, shiftId, date));
        return this;
    }

    public SnapshotBuilder NonCritical(Guid agentId, DateOnly date)
    {
        _nonCritical.Add(new CellKey(agentId, date));
        return this;
    }

    public RecoverySnapshot Build()
    {
        var works = _works.ToDictionary(
            kv => kv.Key,
            kv => (IReadOnlyList<RecoveryWork>)kv.Value);
        return new RecoverySnapshot(
            _days, _agents, works, _availability, _ineligible, _nonCritical, _receivingGroupId);
    }
}
