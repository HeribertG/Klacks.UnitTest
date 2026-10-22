// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Models.Schedules;
using NSubstitute;

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// Answers the order-lineage reads of a substituted IShiftReader (FindFirstByOriginalIdAsync,
/// CountByOriginalIdAsync, AnyByOriginalIdAsync) from an in-memory row list, matching OriginalId and status and
/// taking the first row in the base order (original id, from date, start time), so skill tests keep proving
/// which plannable shift is picked and when an order counts as split. The rows are read on every call, so a test
/// may replace them after wiring. The SQL is pinned by the integration test ShiftReadShapesTests.
/// </summary>
internal static class ShiftLineageStub
{
    /// <param name="reader">Substituted reader to wire</param>
    /// <param name="rows">Current rows, read on every call</param>
    /// <param name="onCall">Invoked once per lineage read, e.g. to record the call order</param>
    public static void Serve(IShiftReader reader, Func<IReadOnlyList<Shift>> rows, Action? onCall = null)
    {
        reader.FindFirstByOriginalIdAsync(Arg.Any<Guid>(), Arg.Any<ShiftStatus>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                onCall?.Invoke();
                return Task.FromResult(Matching(rows(), call.ArgAt<Guid>(0), call.ArgAt<ShiftStatus>(1)).FirstOrDefault());
            });
        reader.CountByOriginalIdAsync(Arg.Any<Guid>(), Arg.Any<ShiftStatus>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                onCall?.Invoke();
                return Task.FromResult(Matching(rows(), call.ArgAt<Guid>(0), call.ArgAt<ShiftStatus>(1)).Count());
            });
        reader.AnyByOriginalIdAsync(Arg.Any<Guid>(), Arg.Any<ShiftStatus>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                onCall?.Invoke();
                return Task.FromResult(Matching(rows(), call.ArgAt<Guid>(0), call.ArgAt<ShiftStatus>(1)).Any());
            });
    }

    private static IEnumerable<Shift> Matching(IReadOnlyList<Shift> rows, Guid originalId, ShiftStatus status) =>
        rows.Where(s => s.OriginalId == originalId && s.Status == status)
            .OrderBy(s => s.OriginalId)
            .ThenBy(s => s.FromDate)
            .ThenBy(s => s.StartShift);
}
