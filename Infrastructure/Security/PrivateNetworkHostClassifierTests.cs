// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PrivateNetworkHostClassifier: private/loopback/link-local IP classification used
/// by both the OAuth2 certificate relaxation and the provider connectivity SSRF guard.
/// </summary>

using System.Net;
using Klacks.Api.Infrastructure.Security;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Security;

[TestFixture]
public class PrivateNetworkHostClassifierTests
{
    [TestCase("127.0.0.1")]
    [TestCase("127.255.255.255")]
    [TestCase("10.0.0.1")]
    [TestCase("10.255.255.255")]
    [TestCase("172.16.0.1")]
    [TestCase("172.31.255.255")]
    [TestCase("192.168.0.1")]
    [TestCase("192.168.255.255")]
    [TestCase("169.254.0.1")]
    [TestCase("169.254.169.254")]
    [TestCase("::1")]
    [TestCase("fc00::1")]
    [TestCase("fd12:3456:789a::1")]
    [TestCase("0.0.0.0")]
    [TestCase("::")]
    [TestCase("::ffff:127.0.0.1")]
    [TestCase("::ffff:169.254.169.254")]
    [TestCase("::ffff:10.0.0.5")]
    public void IsPrivateOrLoopbackAddress_PrivateOrReservedAddress_ReturnsTrue(string address)
    {
        var result = PrivateNetworkHostClassifier.IsPrivateOrLoopbackAddress(IPAddress.Parse(address));

        result.ShouldBeTrue();
    }

    [TestCase("8.8.8.8")]
    [TestCase("1.1.1.1")]
    [TestCase("93.184.216.34")]
    [TestCase("172.15.255.255")]
    [TestCase("172.32.0.0")]
    [TestCase("2606:4700:4700::1111")]
    [TestCase("::ffff:93.184.216.34")]
    public void IsPrivateOrLoopbackAddress_PublicAddress_ReturnsFalse(string address)
    {
        var result = PrivateNetworkHostClassifier.IsPrivateOrLoopbackAddress(IPAddress.Parse(address));

        result.ShouldBeFalse();
    }

    [Test]
    public void IsPrivateOrLoopbackAddress_NullAddress_Throws()
    {
        Should.Throw<ArgumentNullException>(() => PrivateNetworkHostClassifier.IsPrivateOrLoopbackAddress(null!));
    }

    [TestCase("100.64.0.1")]
    [TestCase("100.100.100.200")]
    public void IsPrivateOrLoopbackAddress_CgnatAddress_StaysFalseToKeepTlsRelaxationNarrow(string address)
    {
        var result = PrivateNetworkHostClassifier.IsPrivateOrLoopbackAddress(IPAddress.Parse(address));

        result.ShouldBeFalse();
    }

    [TestCase("100.64.0.1")]
    [TestCase("100.127.255.255")]
    [TestCase("100.100.100.200")]
    [TestCase("0.0.0.1")]
    [TestCase("0.255.255.255")]
    [TestCase("192.0.0.1")]
    [TestCase("198.18.0.1")]
    [TestCase("198.19.255.255")]
    [TestCase("224.0.0.1")]
    [TestCase("239.255.255.255")]
    [TestCase("240.0.0.1")]
    [TestCase("255.255.255.255")]
    [TestCase("fec0::1")]
    [TestCase("ff02::1")]
    [TestCase("::ffff:100.64.0.1")]
    [TestCase("64:ff9b::a9fe:a9fe")]
    [TestCase("2002:a9fe:a9fe::1")]
    [TestCase("::a9fe:a9fe")]
    [TestCase("::7f00:1")]
    [TestCase("127.0.0.1")]
    [TestCase("10.0.0.5")]
    [TestCase("169.254.169.254")]
    [TestCase("::1")]
    [TestCase("fd12:3456:789a::1")]
    public void IsBlockedForOutboundRequest_ReservedOrInternalAddress_ReturnsTrue(string address)
    {
        var result = PrivateNetworkHostClassifier.IsBlockedForOutboundRequest(IPAddress.Parse(address));

        result.ShouldBeTrue();
    }

    [TestCase("100.63.255.255")]
    [TestCase("100.128.0.0")]
    [TestCase("198.17.255.255")]
    [TestCase("198.20.0.0")]
    [TestCase("223.255.255.255")]
    [TestCase("192.0.1.1")]
    [TestCase("64:ff9b::808:808")]
    [TestCase("2002:808:808::1")]
    [TestCase("2606:4700:4700::1111")]
    [TestCase("8.8.8.8")]
    public void IsBlockedForOutboundRequest_PublicAddress_ReturnsFalse(string address)
    {
        var result = PrivateNetworkHostClassifier.IsBlockedForOutboundRequest(IPAddress.Parse(address));

        result.ShouldBeFalse();
    }

    [Test]
    public void IsBlockedForOutboundRequest_NullAddress_Throws()
    {
        Should.Throw<ArgumentNullException>(() => PrivateNetworkHostClassifier.IsBlockedForOutboundRequest(null!));
    }
}
