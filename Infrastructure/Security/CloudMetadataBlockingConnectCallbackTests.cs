// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for CloudMetadataBlockingConnectCallback: the SSRF guard used by the real LLM provider
/// HTTP client. Confirms cloud metadata endpoints are blocked while private-network addresses
/// (needed for on-premises LLM providers) stay reachable.
/// </summary>

using System.Net;
using System.Net.Sockets;
using Klacks.Api.Infrastructure.Security;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Security;

[TestFixture]
public class CloudMetadataBlockingConnectCallbackTests
{
    private const int TestPort = 443;

    private IHostAddressResolver _hostAddressResolver = null!;
    private CloudMetadataBlockingConnectCallback _callback = null!;

    [SetUp]
    public void SetUp()
    {
        _hostAddressResolver = Substitute.For<IHostAddressResolver>();
        _callback = new CloudMetadataBlockingConnectCallback(_hostAddressResolver);
    }

    [TestCase("169.254.169.254")]
    [TestCase("100.100.100.200")]
    public async Task ConnectAsync_ResolvesToCloudMetadataAddress_ThrowsPrivateNetworkAccessBlockedException(string ip)
    {
        _hostAddressResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { IPAddress.Parse(ip) });

        await Should.ThrowAsync<PrivateNetworkAccessBlockedException>(
            async () => await _callback.ConnectAsync("looks-legit.example.com", TestPort, CancellationToken.None));
    }

    [Test]
    public async Task ConnectAsync_HostnameRebindsToMetadataAddress_IsBlockedEvenThoughHostStringLooksPublic()
    {
        _hostAddressResolver.ResolveAsync("looks-public.example.com", Arg.Any<CancellationToken>())
            .Returns(new[] { IPAddress.Parse("169.254.169.254") });

        await Should.ThrowAsync<PrivateNetworkAccessBlockedException>(
            async () => await _callback.ConnectAsync("looks-public.example.com", TestPort, CancellationToken.None));
    }

    [Test]
    public async Task ConnectAsync_NoAddressesResolved_ThrowsSocketException()
    {
        _hostAddressResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Array.Empty<IPAddress>());

        await Should.ThrowAsync<SocketException>(
            async () => await _callback.ConnectAsync("nowhere.example.com", TestPort, CancellationToken.None));
    }
}
