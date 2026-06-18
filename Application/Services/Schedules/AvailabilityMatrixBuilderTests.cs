// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Services.Schedules;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Application.Services.Schedules;

[TestFixture]
public class AvailabilityMatrixBuilderTests
{
    private static readonly DateOnly D = new(2026, 6, 10);
    private static readonly Guid Shift = Guid.NewGuid();

    private static AvailabilityShiftSlot Slot() => new(Shift, D, new TimeOnly(8, 0), new TimeOnly(12, 0));

    private static ClientAvailability Unavailable(int hour) => new() { Date = D, Hour = hour, IsAvailable = false };

    [Test]
    public void Build_BlocksAgentExplicitlyUnavailableDuringTheShift()
    {
        var availability = new Dictionary<string, IReadOnlyList<ClientAvailability>>
        {
            ["agent-a"] = new[] { Unavailable(9) }, // 09:00 is inside 08:00-12:00
        };

        var result = AvailabilityMatrixBuilder.Build([Slot()], availability);

        result.ShouldContain(("agent-a", Shift, D));
    }

    [Test]
    public void Build_DoesNotBlockWhenUnavailabilityIsOutsideTheShift()
    {
        var availability = new Dictionary<string, IReadOnlyList<ClientAvailability>>
        {
            ["agent-b"] = new[] { Unavailable(14) }, // 14:00 is outside 08:00-12:00
        };

        AvailabilityMatrixBuilder.Build([Slot()], availability).ShouldBeEmpty();
    }

    [Test]
    public void Build_TreatsSparseAvailabilityAsAvailable()
    {
        // No availability data for the agent at all -> no block (opt-in/sparse semantics).
        AvailabilityMatrixBuilder.Build([Slot()], new Dictionary<string, IReadOnlyList<ClientAvailability>>())
            .ShouldBeEmpty();
    }

    [Test]
    public void Build_IgnoresAvailabilityRecordsOnOtherDates()
    {
        var availability = new Dictionary<string, IReadOnlyList<ClientAvailability>>
        {
            ["agent-c"] = new[] { new ClientAvailability { Date = D.AddDays(1), Hour = 9, IsAvailable = false } },
        };

        AvailabilityMatrixBuilder.Build([Slot()], availability).ShouldBeEmpty();
    }
}
