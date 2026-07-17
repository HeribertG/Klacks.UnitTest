// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Tests for PrivateNetworkBlockingConnectCallback: the SSRF guard that decides, at actual
/// connect time, whether an outbound connection may proceed. Covers both a directly private/
/// loopback target and a DNS-rebinding scenario where a public-looking hostname resolves to a
/// private address.
/// </summary>

using System.Net;
using System.Net.Sockets;
using Klacks.Api.Infrastructure.Security;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Security;

[TestFixture]
public class PrivateNetworkBlockingConnectCallbackTests
{
    private const int TestPort = 443;

    private IHostAddressResolver _hostAddressResolver = null!;
    private PrivateNetworkBlockingConnectCallback _callback = null!;

    [SetUp]
    public void SetUp()
    {
        _hostAddressResolver = Substitute.For<IHostAddressResolver>();
        _callback = new PrivateNetworkBlockingConnectCallback(_hostAddressResolver);
    }

    [TestCase("127.0.0.1")]
    [TestCase("169.254.169.254")]
    [TestCase("10.0.0.5")]
    [TestCase("::1")]
    public async Task ConnectAsync_ResolvesToPrivateOrLoopbackAddress_ThrowsPrivateNetworkAccessBlockedException(string ip)
    {
        _hostAddressResolver.ResolveAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(new[] { IPAddress.Parse(ip) });

        await Should.ThrowAsync<PrivateNetworkAccessBlockedException>(
            async () => await _callback.ConnectAsync("looks-public.example.com", TestPort, CancellationToken.None));
    }

    [Test]
    public async Task ConnectAsync_HostnameRebindsToPrivateAddress_IsBlockedEvenThoughHostStringLooksPublic()
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
