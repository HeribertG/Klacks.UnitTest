// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PlaygroundVisitorMiddleware: with the mode off nothing is read or validated and no visitor is
/// published; with the mode on the normalized IP and the validated device id are visible to everything below it,
/// including detached tasks, and the context is gone afterwards.
/// </summary>

using System.Net;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Models.Playground;
using Klacks.Api.Domain.Services.Playground;
using Klacks.Api.Infrastructure.Middleware;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundVisitorMiddlewareTests
{
    private IPlaygroundMode _mode = null!;
    private IPlaygroundDeviceTokenService _tokens = null!;

    [SetUp]
    public void SetUp()
    {
        _mode = Substitute.For<IPlaygroundMode>();
        _tokens = Substitute.For<IPlaygroundDeviceTokenService>();
        PlaygroundVisitorContext.Clear();
    }

    [TearDown]
    public void TearDown()
    {
        PlaygroundVisitorContext.Clear();
    }

    private static DefaultHttpContext Context(string? remoteIp, string? deviceHeader)
    {
        var context = new DefaultHttpContext();
        if (remoteIp is not null)
        {
            context.Connection.RemoteIpAddress = IPAddress.Parse(remoteIp);
        }

        if (deviceHeader is not null)
        {
            context.Request.Headers[PlaygroundTokenLimitConstants.DeviceHeaderName] = deviceHeader;
        }

        return context;
    }

    [Test]
    public async Task ModeOff_ForwardsTheRequestAndTouchesNothing()
    {
        _mode.IsActive.Returns(false);
        var nextCalled = false;
        PlaygroundVisitor? seen = new("sentinel", null);
        var sut = new PlaygroundVisitorMiddleware(_ =>
        {
            nextCalled = true;
            seen = PlaygroundVisitorContext.Current;
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(Context("203.0.113.9", "some-token"), _mode, _tokens);

        nextCalled.ShouldBeTrue();
        seen.ShouldBeNull();
        _tokens.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public async Task ModeOn_PublishesNormalizedIpAndValidatedDeviceToEverythingBelow()
    {
        _mode.IsActive.Returns(true);
        _tokens.TryGetDeviceId("good-token").Returns("device-1");
        PlaygroundVisitor? seen = null;
        PlaygroundVisitor? seenInDetachedTask = null;
        var sut = new PlaygroundVisitorMiddleware(async _ =>
        {
            seen = PlaygroundVisitorContext.Current;
            await Task.Run(() => seenInDetachedTask = PlaygroundVisitorContext.Current);
        });

        await sut.InvokeAsync(Context("::ffff:203.0.113.9", "good-token"), _mode, _tokens);

        seen.ShouldNotBeNull();
        seen.IpBucketKey.ShouldBe("203.0.113.9");
        seen.DeviceId.ShouldBe("device-1");
        seenInDetachedTask.ShouldBeSameAs(seen);
    }

    [Test]
    public async Task ModeOn_InvalidOrMissingDeviceHeader_YieldsAVisitorWithoutDevice()
    {
        _mode.IsActive.Returns(true);
        _tokens.TryGetDeviceId(Arg.Any<string?>()).Returns((string?)null);
        PlaygroundVisitor? seen = null;
        var sut = new PlaygroundVisitorMiddleware(_ =>
        {
            seen = PlaygroundVisitorContext.Current;
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(Context("2001:db8:1:2:3:4:5:6", "forged"), _mode, _tokens);

        seen.ShouldNotBeNull();
        seen.DeviceId.ShouldBeNull();
        seen.IpBucketKey.ShouldBe("2001:db8:1:2::/64");
    }

    [Test]
    public async Task ModeOn_UnknownRemoteAddress_StillPublishesAVisitor()
    {
        _mode.IsActive.Returns(true);
        PlaygroundVisitor? seen = null;
        var sut = new PlaygroundVisitorMiddleware(_ =>
        {
            seen = PlaygroundVisitorContext.Current;
            return Task.CompletedTask;
        });

        await sut.InvokeAsync(Context(null, null), _mode, _tokens);

        seen.ShouldNotBeNull();
        seen.IpBucketKey.ShouldBeNull();
    }

    [Test]
    public async Task ModeOn_ClearsTheContextAfterTheRequest_EvenWhenThePipelineThrows()
    {
        _mode.IsActive.Returns(true);
        var sut = new PlaygroundVisitorMiddleware(_ => throw new InvalidOperationException("boom"));

        await Should.ThrowAsync<InvalidOperationException>(
            async () => await sut.InvokeAsync(Context("203.0.113.9", null), _mode, _tokens));

        PlaygroundVisitorContext.Current.ShouldBeNull();
    }

    [Test]
    public async Task VisitorFlag_RaisedInADetachedTask_IsVisibleToTheRequest()
    {
        _mode.IsActive.Returns(true);
        PlaygroundVisitor? seen = null;
        var sut = new PlaygroundVisitorMiddleware(async _ =>
        {
            seen = PlaygroundVisitorContext.Current;
            await Task.Run(() => PlaygroundVisitorContext.Current!.MarkTokenLimitHit());
        });

        await sut.InvokeAsync(Context("203.0.113.9", null), _mode, _tokens);

        seen!.TokenLimitHit.ShouldBeTrue();
    }
}
