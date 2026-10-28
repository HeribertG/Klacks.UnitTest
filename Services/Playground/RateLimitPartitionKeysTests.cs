// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for RateLimitPartitionKeys: without the Playground mode the bucket is exactly the user name, else the remote
/// address, else "anonymous" (unchanged for customer installations); with the mode on it is the IP bucket plus the device
/// id, so visitors who are all the same demo admin do not share one bucket. The status policy always partitions by the
/// normalized network address. Also pins the status endpoint to its own policy instead of the login limit.
/// </summary>

using System.Net;
using System.Reflection;
using System.Security.Claims;
using Klacks.Api.Application.Constants;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Models.Playground;
using Klacks.Api.Domain.Services.Playground;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Presentation.Controllers.UserBackend.Playground;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class RateLimitPartitionKeysTests
{
    private const string UserName = "admin@test.com";
    private const string Address = "203.0.113.7";

    [TearDown]
    public void TearDown()
    {
        PlaygroundVisitorContext.Clear();
    }

    private static DefaultHttpContext Context(bool playground, string? user, string? address)
    {
        var mode = Substitute.For<IPlaygroundMode>();
        mode.IsActive.Returns(playground);
        var context = new DefaultHttpContext
        {
            RequestServices = new ServiceCollection().AddSingleton(mode).BuildServiceProvider(),
        };
        if (user is not null)
        {
            context.User = new ClaimsPrincipal(new ClaimsIdentity(new[] { new Claim(ClaimTypes.Name, user) }, "test", ClaimTypes.Name, ClaimTypes.Role));
        }

        context.Connection.RemoteIpAddress = address is null ? null : IPAddress.Parse(address);

        return context;
    }

    [Test]
    public void ModeOff_UserNameWins()
    {
        RateLimitPartitionKeys.ForUserOrVisitor(Context(false, UserName, Address)).ShouldBe(UserName);
    }

    [Test]
    public void ModeOff_WithoutUser_UsesTheRemoteAddressAsIs()
    {
        RateLimitPartitionKeys.ForUserOrVisitor(Context(false, null, Address)).ShouldBe(Address);
    }

    [Test]
    public void ModeOff_WithoutUserAndAddress_IsAnonymous()
    {
        RateLimitPartitionKeys.ForUserOrVisitor(Context(false, null, null)).ShouldBe(RateLimitingPolicies.AnonymousPartitionKey);
    }

    [Test]
    public void ModeOff_IgnoresTheAmbientVisitor()
    {
        PlaygroundVisitorContext.Set(new PlaygroundVisitor("198.51.100.1", "device-x"));

        RateLimitPartitionKeys.ForUserOrVisitor(Context(false, UserName, Address)).ShouldBe(UserName);
    }

    [Test]
    public void ModeOn_TwoVisitorsWithTheSameUserNameGetDifferentBuckets()
    {
        PlaygroundVisitorContext.Set(new PlaygroundVisitor("198.51.100.1", "device-a"));
        var first = RateLimitPartitionKeys.ForUserOrVisitor(Context(true, UserName, "198.51.100.1"));
        PlaygroundVisitorContext.Set(new PlaygroundVisitor("198.51.100.2", "device-b"));
        var second = RateLimitPartitionKeys.ForUserOrVisitor(Context(true, UserName, "198.51.100.2"));

        first.ShouldNotBe(second);
        first.ShouldNotContain(UserName);
    }

    [Test]
    public void ModeOn_SameIpDifferentDevices_GetDifferentBuckets()
    {
        PlaygroundVisitorContext.Set(new PlaygroundVisitor(Address, "device-a"));
        var first = RateLimitPartitionKeys.ForUserOrVisitor(Context(true, UserName, Address));
        PlaygroundVisitorContext.Set(new PlaygroundVisitor(Address, "device-b"));
        var second = RateLimitPartitionKeys.ForUserOrVisitor(Context(true, UserName, Address));

        first.ShouldNotBe(second);
    }

    [Test]
    public void ModeOn_SameVisitor_GetsTheSameBucket()
    {
        PlaygroundVisitorContext.Set(new PlaygroundVisitor(Address, "device-a"));

        RateLimitPartitionKeys.ForUserOrVisitor(Context(true, UserName, Address))
            .ShouldBe(RateLimitPartitionKeys.ForUserOrVisitor(Context(true, UserName, Address)));
    }

    [Test]
    public void ModeOn_WithoutVisitor_FallsBackToTheNormalizedAddressAndNoDevice()
    {
        var key = RateLimitPartitionKeys.ForUserOrVisitor(Context(true, UserName, "2001:db8:1:2:3:4:5:6"));

        key.ShouldContain("2001:db8:1:2::/64");
        key.ShouldEndWith(RateLimitingPolicies.PlaygroundNoDeviceKey);
    }

    [Test]
    public void ModeOn_WithoutVisitorOrAddress_IsStillAKey()
    {
        var key = RateLimitPartitionKeys.ForUserOrVisitor(Context(true, null, null));

        key.ShouldContain(RateLimitingPolicies.AnonymousPartitionKey);
    }

    [Test]
    public void NetworkAddress_IsNormalizedInEveryMode()
    {
        RateLimitPartitionKeys.ForNetworkAddress(Context(false, UserName, "2001:db8:1:2:3:4:5:6")).ShouldBe("2001:db8:1:2::/64");
        RateLimitPartitionKeys.ForNetworkAddress(Context(true, UserName, Address)).ShouldBe(Address);
        RateLimitPartitionKeys.ForNetworkAddress(Context(false, null, null)).ShouldBe(RateLimitingPolicies.AnonymousPartitionKey);
    }

    [Test]
    public void StatusEndpoint_UsesItsOwnPolicyAndNotTheLoginLimit()
    {
        var action = typeof(PlaygroundController).GetMethod(nameof(PlaygroundController.GetStatus))!;

        var policy = action.GetCustomAttribute<EnableRateLimitingAttribute>().ShouldNotBeNull().PolicyName;

        policy.ShouldBe(RateLimitingPolicies.PlaygroundStatus);
        policy.ShouldNotBe(RateLimitingPolicies.Login);
        RateLimitingPolicies.PlaygroundStatusPermitLimit.ShouldBeGreaterThan(RateLimitingPolicies.LoginPermitLimit);
    }
}
