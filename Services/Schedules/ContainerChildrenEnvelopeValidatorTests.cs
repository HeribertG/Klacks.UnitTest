// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Services.Schedules;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Services.Schedules;

[TestFixture]
public class ContainerChildrenEnvelopeValidatorTests
{
    private static TimeOnly T(int h, int m = 0) => new(h, m);

    [Test]
    public void ItemsInsideTheEnvelope_HaveNoOverhang()
    {
        var items = new[] { (T(7, 15), T(8, 30)), (T(10, 30), T(11, 30)) };

        ContainerChildrenEnvelopeValidator.FindOverhangs(T(7), T(12), items).ShouldBeEmpty();
    }

    [Test]
    public void ItemsTouchingTheEnvelopeBounds_HaveNoOverhang()
    {
        var items = new[] { (T(7), T(9)), (T(11), T(12)) };

        ContainerChildrenEnvelopeValidator.FindOverhangs(T(7), T(12), items).ShouldBeEmpty();
    }

    [Test]
    public void ItemCutThroughByTheEnvelopeEnd_IsReported()
    {
        var items = new[] { (T(7, 15), T(8, 30)), (T(10, 30), T(11, 30)) };

        var overhangs = ContainerChildrenEnvelopeValidator.FindOverhangs(T(7), T(11), items);

        overhangs.ShouldBe(new[] { (T(10, 30), T(11, 30)) });
    }

    [Test]
    public void ItemCutThroughByTheEnvelopeStart_IsReported()
    {
        var items = new[] { (T(10, 30), T(11, 30)), (T(13), T(14, 30)) };

        var overhangs = ContainerChildrenEnvelopeValidator.FindOverhangs(T(11), T(15), items);

        overhangs.ShouldBe(new[] { (T(10, 30), T(11, 30)) });
    }

    [Test]
    public void ItemCompletelyOutsideTheEnvelope_IsReported()
    {
        var items = new[] { (T(13), T(14, 30)) };

        var overhangs = ContainerChildrenEnvelopeValidator.FindOverhangs(T(7), T(11), items);

        overhangs.ShouldBe(new[] { (T(13), T(14, 30)) });
    }

    [Test]
    public void ItemsOfAnEnvelopeCrossingMidnight_AreCheckedRelativeToItsStart()
    {
        var inside = new[] { (T(22, 30), T(23, 30)), (T(23, 45), T(0, 30)), (T(1), T(2)) };
        var outside = new[] { (T(3), T(5)) };

        ContainerChildrenEnvelopeValidator.FindOverhangs(T(22), T(4), inside).ShouldBeEmpty();
        ContainerChildrenEnvelopeValidator.FindOverhangs(T(22), T(4), outside).ShouldBe(outside);
    }

    [Test]
    public void EnvelopeWithEqualStartAndEnd_CoversTheWholeDay()
    {
        var items = new[] { (T(5), T(9)), (T(23), T(1)) };

        ContainerChildrenEnvelopeValidator.FindOverhangs(T(6), T(6), items).ShouldBeEmpty();
    }

    [Test]
    public void EnvelopeIgnoresSeconds()
    {
        var items = new[] { (new TimeOnly(7, 0, 30), new TimeOnly(10, 59, 59)) };

        ContainerChildrenEnvelopeValidator.FindOverhangs(T(7), T(11), items).ShouldBeEmpty();
    }
}
