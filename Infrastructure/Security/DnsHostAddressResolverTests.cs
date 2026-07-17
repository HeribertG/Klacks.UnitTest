// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for DnsHostAddressResolver: IP-literal fast path and real hostname resolution.
/// Uses "localhost" rather than an external domain so the suite has no dependency on network
/// access - resolution goes through the local hosts file/OS resolver only.
/// </summary>

using System.Net;
using Klacks.Api.Infrastructure.Security;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Security;

[TestFixture]
public class DnsHostAddressResolverTests
{
    private DnsHostAddressResolver _resolver = null!;

    [SetUp]
    public void SetUp() => _resolver = new DnsHostAddressResolver();

    [Test]
    public async Task ResolveAsync_IpLiteral_ReturnsParsedAddressWithoutDns()
    {
        var result = await _resolver.ResolveAsync("169.254.169.254");

        result.ShouldBe(new[] { IPAddress.Parse("169.254.169.254") });
    }

    [Test]
    public async Task ResolveAsync_Localhost_ResolvesToLoopbackAddress()
    {
        var result = await _resolver.ResolveAsync("localhost");

        result.ShouldNotBeEmpty();
        result.ShouldAllBe(ip => PrivateNetworkHostClassifier.IsPrivateOrLoopbackAddress(ip));
    }
}
