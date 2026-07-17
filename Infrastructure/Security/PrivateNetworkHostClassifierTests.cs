// Copyright (c) Heribert Gasparoli Private. All rights reserved.

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
}
