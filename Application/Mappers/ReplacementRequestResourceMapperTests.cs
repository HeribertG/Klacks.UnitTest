// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The resource of a request row carries its kind and the reversal of an applied move.
/// </summary>

using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Models.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.Mappers;

[TestFixture]
public class ReplacementRequestResourceMapperTests
{
    private const int ShortNoticeHours = 24;

    [Test]
    public void AMoveRow_CarriesItsKindAndReversal()
    {
        var reversedAt = new DateTime(2026, 11, 4, 9, 0, 0, DateTimeKind.Utc);
        var row = new ReplacementRequest
        {
            Id = Guid.NewGuid(),
            Kind = ReplacementRequestKind.Move,
            ReversedAtUtc = reversedAt,
            ReportedAtUtc = new DateTime(2026, 11, 3, 6, 0, 0, DateTimeKind.Utc),
            ShiftStartUtc = new DateTime(2026, 11, 4, 7, 0, 0, DateTimeKind.Utc),
        };

        var resource = ReplacementRequestResourceMapper.ToResource(row, ShortNoticeHours, false);

        resource.Kind.ShouldBe(ReplacementRequestKind.Move);
        resource.ReversedAtUtc.ShouldBe(reversedAt);
    }

    [Test]
    public void AReplacementRow_IsNotReversed()
    {
        var resource = ReplacementRequestResourceMapper.ToResource(new ReplacementRequest { Id = Guid.NewGuid() }, ShortNoticeHours, false);

        resource.Kind.ShouldBe(ReplacementRequestKind.Replacement);
        resource.ReversedAtUtc.ShouldBeNull();
    }
}
