// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for CloudMetadataHostClassifier: cloud instance-metadata IP classification used by the
/// LLM provider SSRF guard, which must NOT flag ordinary private/loopback addresses (unlike
/// PrivateNetworkHostClassifier), since on-premises LLM providers legitimately run on those.
/// </summary>

using System.Net;
using Klacks.Api.Infrastructure.Security;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Security;

[TestFixture]
public class CloudMetadataHostClassifierTests
{
    [TestCase("169.254.169.254")]
    [TestCase("100.100.100.200")]
    [TestCase("fd00:ec2::254")]
    [TestCase("::ffff:169.254.169.254")]
    public void IsCloudMetadataAddress_KnownMetadataEndpoint_ReturnsTrue(string address)
    {
        var result = CloudMetadataHostClassifier.IsCloudMetadataAddress(IPAddress.Parse(address));

        result.ShouldBeTrue();
    }

    [TestCase("127.0.0.1")]
    [TestCase("10.0.0.5")]
    [TestCase("192.168.1.163")]
    [TestCase("172.16.0.1")]
    [TestCase("169.254.0.1")]
    [TestCase("8.8.8.8")]
    [TestCase("::1")]
    public void IsCloudMetadataAddress_PrivateOrPublicNonMetadataAddress_ReturnsFalse(string address)
    {
        var result = CloudMetadataHostClassifier.IsCloudMetadataAddress(IPAddress.Parse(address));

        result.ShouldBeFalse();
    }

    [Test]
    public void IsCloudMetadataAddress_NullAddress_Throws()
    {
        Should.Throw<ArgumentNullException>(() => CloudMetadataHostClassifier.IsCloudMetadataAddress(null!));
    }
}
