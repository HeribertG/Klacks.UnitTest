// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PlaygroundIpNormalizer: IPv4 stays as is, IPv4-mapped IPv6 is folded onto its IPv4 address first,
/// and real IPv6 addresses are cut to their /64 network so one customer line owns one bucket.
/// </summary>

using System.Net;
using Klacks.Api.Domain.Services.Playground;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundIpNormalizerTests
{
    [Test]
    public void Normalize_Null_ReturnsNull()
    {
        PlaygroundIpNormalizer.Normalize(null).ShouldBeNull();
    }

    [Test]
    public void Normalize_IPv4_IsKeptUnchanged()
    {
        PlaygroundIpNormalizer.Normalize(IPAddress.Parse("203.0.113.7")).ShouldBe("203.0.113.7");
    }

    [Test]
    public void Normalize_IPv4MappedIPv6_FoldsOntoIPv4()
    {
        var mapped = IPAddress.Parse("::ffff:203.0.113.7");

        PlaygroundIpNormalizer.Normalize(mapped).ShouldBe("203.0.113.7");
    }

    [Test]
    public void Normalize_IPv4AndItsMappedForm_ShareOneBucketKey()
    {
        var plain = PlaygroundIpNormalizer.Normalize(IPAddress.Parse("198.51.100.20"));
        var mapped = PlaygroundIpNormalizer.Normalize(IPAddress.Parse("::ffff:198.51.100.20"));

        mapped.ShouldBe(plain);
    }

    [Test]
    public void Normalize_IPv6_IsCutToItsSlash64()
    {
        var key = PlaygroundIpNormalizer.Normalize(IPAddress.Parse("2001:db8:abcd:12:aaaa:bbbb:cccc:dddd"));

        key.ShouldBe("2001:db8:abcd:12::/64");
    }

    [Test]
    public void Normalize_TwoHostsInTheSameSlash64_ShareOneBucketKey()
    {
        var first = PlaygroundIpNormalizer.Normalize(IPAddress.Parse("2001:db8:abcd:12::1"));
        var second = PlaygroundIpNormalizer.Normalize(IPAddress.Parse("2001:db8:abcd:12:ffff:ffff:ffff:ffff"));

        second.ShouldBe(first);
    }

    [Test]
    public void Normalize_TwoDifferentSlash64Networks_GetDifferentKeys()
    {
        var first = PlaygroundIpNormalizer.Normalize(IPAddress.Parse("2001:db8:abcd:12::1"));
        var second = PlaygroundIpNormalizer.Normalize(IPAddress.Parse("2001:db8:abcd:13::1"));

        second.ShouldNotBe(first);
    }

    [Test]
    public void Normalize_IPv6Key_DoesNotCollideWithAnIPv4Key()
    {
        var v6 = PlaygroundIpNormalizer.Normalize(IPAddress.Parse("2001:db8::1"));
        var v4 = PlaygroundIpNormalizer.Normalize(IPAddress.Parse("32.1.13.184"));

        v6.ShouldNotBe(v4);
    }
}
