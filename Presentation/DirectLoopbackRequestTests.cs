// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins which requests count as coming straight from the machine itself: only a loopback remote address without any
/// proxy header. Everything else (LAN address, missing address, nginx headers) counts as remote, so the first start
/// asks for the setup code.
/// </summary>

using System.Net;
using Klacks.Api.Presentation.Authentication;
using Microsoft.AspNetCore.Http;

namespace Klacks.UnitTest.Presentation;

[TestFixture]
public class DirectLoopbackRequestTests
{
    private static DefaultHttpContext Request(string? remoteAddress, string? header = null)
    {
        var context = new DefaultHttpContext();
        context.Connection.RemoteIpAddress = remoteAddress is null ? null : IPAddress.Parse(remoteAddress);
        if (header is not null)
        {
            context.Request.Headers[header] = "192.168.1.20";
        }

        return context;
    }

    [TestCase("127.0.0.1")]
    [TestCase("::1")]
    [TestCase("::ffff:127.0.0.1")]
    public void LoopbackWithoutProxyHeaders_IsDirect(string address)
    {
        DirectLoopbackRequest.IsDirectLoopback(Request(address)).ShouldBeTrue();
    }

    [TestCase("192.168.1.20")]
    [TestCase("172.18.0.5")]
    [TestCase("10.0.0.1")]
    [TestCase(null)]
    public void OtherOrMissingAddress_IsNotDirect(string? address)
    {
        DirectLoopbackRequest.IsDirectLoopback(Request(address)).ShouldBeFalse();
    }

    [TestCase("X-Forwarded-For")]
    [TestCase("X-Original-For")]
    [TestCase("Forwarded")]
    [TestCase("X-Real-IP")]
    public void LoopbackBehindAProxy_IsNotDirect(string header)
    {
        DirectLoopbackRequest.IsDirectLoopback(Request("127.0.0.1", header)).ShouldBeFalse();
    }
}
