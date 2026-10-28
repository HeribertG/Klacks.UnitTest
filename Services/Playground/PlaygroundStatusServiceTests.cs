// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for the Playground status answer and its controller: with the mode off nothing is minted and the answer
/// is {limitActive:false, deviceToken:null}; with the mode on a caller without a valid device gets a new token and
/// a caller with one gets null. The JSON keeps deviceToken explicitly (null, not omitted), the endpoint is
/// anonymous and sits under the per-IP login rate limit. Also pins the SSE error chunk field name.
/// </summary>

using System.Reflection;
using System.Text.Json;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Playground;
using Klacks.Api.Application.Interfaces.Playground;
using Klacks.Api.Application.Services.Playground;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Playground;
using Klacks.Api.Domain.Services.Playground;
using Klacks.Api.Presentation.Controllers.UserBackend.Playground;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.RateLimiting;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundStatusServiceTests
{
    private const string MintedToken = "minted-token";

    private IPlaygroundMode _mode = null!;
    private IPlaygroundDeviceTokenService _tokens = null!;
    private PlaygroundStatusService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _mode = Substitute.For<IPlaygroundMode>();
        _tokens = Substitute.For<IPlaygroundDeviceTokenService>();
        _tokens.Issue().Returns(MintedToken);
        _sut = new PlaygroundStatusService(_mode, _tokens);
    }

    [Test]
    public void ModeOff_ReportsInactive_AndMintsNothing()
    {
        _mode.IsActive.Returns(false);

        var status = _sut.GetStatus(new PlaygroundVisitor("203.0.113.1", null));

        status.ShouldBe(new PlaygroundStatusResource(false, null));
        _tokens.ReceivedCalls().ShouldBeEmpty();
    }

    [Test]
    public void ModeOn_VisitorWithoutValidDevice_GetsANewToken()
    {
        _mode.IsActive.Returns(true);

        var status = _sut.GetStatus(new PlaygroundVisitor("203.0.113.1", null));

        status.LimitActive.ShouldBeTrue();
        status.DeviceToken.ShouldBe(MintedToken);
    }

    [Test]
    public void ModeOn_NoVisitorAtAll_GetsANewToken()
    {
        _mode.IsActive.Returns(true);

        _sut.GetStatus(null).DeviceToken.ShouldBe(MintedToken);
    }

    [Test]
    public void ModeOn_VisitorWithValidDevice_GetsNoToken()
    {
        _mode.IsActive.Returns(true);

        var status = _sut.GetStatus(new PlaygroundVisitor("203.0.113.1", "device-1"));

        status.LimitActive.ShouldBeTrue();
        status.DeviceToken.ShouldBeNull();
        _tokens.DidNotReceive().Issue();
    }

    [Test]
    public void Resource_SerializesWithCamelCaseNamesAndAnExplicitNullToken()
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web) { DefaultIgnoreCondition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull };

        var json = JsonSerializer.Serialize(new PlaygroundStatusResource(true, null), options);

        json.ShouldBe("{\"limitActive\":true,\"deviceToken\":null}");
    }

    [Test]
    public void Resource_WithToken_SerializesTheToken()
    {
        var json = JsonSerializer.Serialize(new PlaygroundStatusResource(true, "abc"), new JsonSerializerOptions(JsonSerializerDefaults.Web));

        json.ShouldBe("{\"limitActive\":true,\"deviceToken\":\"abc\"}");
    }

    [Test]
    public void Controller_ReturnsTheServiceAnswerForTheAmbientVisitor()
    {
        var statusService = Substitute.For<IPlaygroundStatusService>();
        var visitor = new PlaygroundVisitor("203.0.113.1", "device-1");
        var expected = new PlaygroundStatusResource(true, null);
        statusService.GetStatus(visitor).Returns(expected);
        PlaygroundVisitorContext.Set(visitor);
        try
        {
            var result = new PlaygroundController(statusService).GetStatus();

            result.Result.ShouldBeOfType<OkObjectResult>().Value.ShouldBeSameAs(expected);
        }
        finally
        {
            PlaygroundVisitorContext.Clear();
        }
    }

    [Test]
    public void Controller_IsAnonymousAndUnderItsOwnRateLimit()
    {
        typeof(PlaygroundController).GetCustomAttribute<AllowAnonymousAttribute>().ShouldNotBeNull();
        typeof(PlaygroundController).GetCustomAttribute<AuthorizeAttribute>().ShouldBeNull();

        var action = typeof(PlaygroundController).GetMethod(nameof(PlaygroundController.GetStatus))!;
        action.GetCustomAttribute<EnableRateLimitingAttribute>()!.PolicyName.ShouldBe(RateLimitingPolicies.PlaygroundStatus);
    }

    [Test]
    public void Controller_RouteIsApiBackendPlaygroundStatus()
    {
        var route = typeof(PlaygroundController).GetCustomAttribute<RouteAttribute>()!.Template;
        var action = typeof(PlaygroundController).GetMethod(nameof(PlaygroundController.GetStatus))!
            .GetCustomAttribute<HttpGetAttribute>()!.Template;

        $"/{route}/{action}".ShouldBe("/api/backend/Playground/Status");
    }

    [Test]
    public void SseErrorChunk_SerializesTheCodeAsErrorCode()
    {
        var chunk = SseChunk.Error("message");
        chunk.ErrorCode = PlaygroundTokenLimitConstants.ErrorCode;

        var json = JsonSerializer.Serialize(chunk, SseChunkJson.Options);

        json.ShouldContain("\"errorCode\":\"PLAYGROUND_TOKEN_LIMIT\"");
    }

    [Test]
    public void SseErrorChunk_WithoutCode_OmitsTheField()
    {
        JsonSerializer.Serialize(SseChunk.Error("message"), SseChunkJson.Options).ShouldNotContain("errorCode");
    }

    [Test]
    public void Contract_ConstantsMatchTheFrozenValues()
    {
        PlaygroundTokenLimitConstants.DeviceHeaderName.ShouldBe("X-Playground-Device");
        PlaygroundTokenLimitConstants.ErrorCode.ShouldBe("PLAYGROUND_TOKEN_LIMIT");
        PlaygroundTokenLimitOptions.SectionName.ShouldBe("Playground:TokenLimit");
    }
}
