// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PlaygroundPromptLimitFilter: with the mode on a chat message, transcription text or string skill
/// parameter of exactly 1000 characters passes and 1001 is answered with HTTP 400 and errorCode
/// PLAYGROUND_PROMPT_TOO_LONG before the action runs; with the mode off there is no limit. Also pins the attribute
/// on the chat actions and the transcription endpoint.
/// </summary>

using System.Reflection;
using System.Text.Json;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Presentation.Controllers.Assistant;
using Klacks.Api.Presentation.DTOs.Assistant;
using Klacks.Api.Presentation.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.Abstractions;
using Microsoft.AspNetCore.Mvc.Filters;
using Microsoft.AspNetCore.Routing;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundPromptLimitFilterTests
{
    private const int Limit = PlaygroundHardeningConstants.PlaygroundMaxPromptChars;

    private IPlaygroundMode _mode = null!;
    private PlaygroundPromptLimitFilter _sut = null!;
    private ActionContext _actionContext = null!;

    [SetUp]
    public void SetUp()
    {
        _mode = Substitute.For<IPlaygroundMode>();
        _sut = new PlaygroundPromptLimitFilter(_mode);
        _actionContext = new ActionContext(new DefaultHttpContext(), new RouteData(), new ActionDescriptor());
    }

    private async Task<(IActionResult? Result, bool Ran)> Run(object argument)
    {
        var executing = new ActionExecutingContext(
            _actionContext,
            new List<IFilterMetadata>(),
            new Dictionary<string, object?> { ["request"] = argument },
            new object());
        var ran = false;

        await _sut.OnActionExecutionAsync(executing, () =>
        {
            ran = true;
            return Task.FromResult(new ActionExecutedContext(_actionContext, new List<IFilterMetadata>(), new object()));
        });

        return (executing.Result, ran);
    }

    private static void AssertTooLong(IActionResult? result)
    {
        var objectResult = result.ShouldBeOfType<ObjectResult>();
        objectResult.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        var problem = objectResult.Value.ShouldBeOfType<ProblemDetails>();
        problem.Extensions["errorCode"].ShouldBe(PlaygroundHardeningConstants.PromptTooLongErrorCode);
        problem.Detail.ShouldBe(PlaygroundHardeningConstants.PromptTooLongMessage);
    }

    [Test]
    public async Task Chat_ExactlyTheLimit_Passes()
    {
        _mode.IsActive.Returns(true);

        var (result, ran) = await Run(new LLMRequest { Message = new string('a', Limit) });

        ran.ShouldBeTrue();
        result.ShouldBeNull();
    }

    [Test]
    public async Task Chat_OneOverTheLimit_IsRejectedBeforeTheActionRuns()
    {
        _mode.IsActive.Returns(true);

        var (result, ran) = await Run(new LLMRequest { Message = new string('a', Limit + 1) });

        ran.ShouldBeFalse();
        AssertTooLong(result);
    }

    [Test]
    public async Task Chat_ModeOff_HasNoLimit()
    {
        _mode.IsActive.Returns(false);

        var (result, ran) = await Run(new LLMRequest { Message = new string('a', Limit * 20) });

        ran.ShouldBeTrue();
        result.ShouldBeNull();
    }

    [Test]
    public async Task Transcription_OneOverTheLimit_IsRejected()
    {
        _mode.IsActive.Returns(true);

        var (result, ran) = await Run(new TranscriptionEnhanceRequest { RawText = new string('b', Limit + 1), Locale = "de" });

        ran.ShouldBeFalse();
        AssertTooLong(result);
    }

    [Test]
    public async Task Transcription_ExactlyTheLimit_Passes()
    {
        _mode.IsActive.Returns(true);

        var (_, ran) = await Run(new TranscriptionEnhanceRequest { RawText = new string('b', Limit), Locale = "de" });

        ran.ShouldBeTrue();
    }

    [Test]
    public async Task Transcription_ModeOff_HasNoLimit()
    {
        _mode.IsActive.Returns(false);

        var (_, ran) = await Run(new TranscriptionEnhanceRequest { RawText = new string('b', Limit * 5), Locale = "de" });

        ran.ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteFunction_LongStringParameter_IsRejected()
    {
        _mode.IsActive.Returns(true);
        var request = new LLMFunctionExecuteRequest
        {
            FunctionName = "search",
            Parameters = new Dictionary<string, object> { ["query"] = new string('c', Limit + 1) },
        };

        var (result, ran) = await Run(request);

        ran.ShouldBeFalse();
        AssertTooLong(result);
    }

    [Test]
    public async Task ExecuteFunction_LongJsonStringParameter_IsRejected()
    {
        _mode.IsActive.Returns(true);
        var json = JsonSerializer.Deserialize<JsonElement>($"\"{new string('d', Limit + 1)}\"");
        var request = new LLMFunctionExecuteRequest
        {
            FunctionName = "search",
            Parameters = new Dictionary<string, object> { ["query"] = json },
        };

        var (result, ran) = await Run(request);

        ran.ShouldBeFalse();
        AssertTooLong(result);
    }

    [Test]
    public async Task ExecuteFunction_ShortAndNonStringParameters_Pass()
    {
        _mode.IsActive.Returns(true);
        var request = new LLMFunctionExecuteRequest
        {
            FunctionName = "search",
            Parameters = new Dictionary<string, object> { ["query"] = new string('c', Limit), ["count"] = 5000, ["flag"] = true },
        };

        var (_, ran) = await Run(request);

        ran.ShouldBeTrue();
    }

    [Test]
    public async Task ExecuteFunctionBatch_OneLongParameter_RejectsTheBatch()
    {
        _mode.IsActive.Returns(true);
        var batch = new List<LLMFunctionExecuteRequest>
        {
            new() { FunctionName = "a", Parameters = new Dictionary<string, object> { ["q"] = "short" } },
            new() { FunctionName = "b", Parameters = new Dictionary<string, object> { ["q"] = new string('e', Limit + 1) } },
        };

        var (result, ran) = await Run(batch);

        ran.ShouldBeFalse();
        AssertTooLong(result);
    }

    [Test]
    public async Task ExecuteFunction_ModeOff_HasNoLimit()
    {
        _mode.IsActive.Returns(false);
        var request = new LLMFunctionExecuteRequest
        {
            FunctionName = "search",
            Parameters = new Dictionary<string, object> { ["query"] = new string('c', Limit * 3) },
        };

        var (_, ran) = await Run(request);

        ran.ShouldBeTrue();
    }

    [Test]
    public void MaxPromptChars_IsOneThousand()
    {
        Limit.ShouldBe(1000);
    }

    [TestCase(typeof(ChatController), nameof(ChatController.ProcessMessage))]
    [TestCase(typeof(ChatController), nameof(ChatController.ProcessMessageStream))]
    [TestCase(typeof(ChatController), nameof(ChatController.ExecuteFunction))]
    [TestCase(typeof(ChatController), nameof(ChatController.ExecuteFunctionsBatch))]
    [TestCase(typeof(TranscriptionController), nameof(TranscriptionController.Enhance))]
    public void FreeTextEndpoint_CarriesThePromptLimit(Type controller, string method)
    {
        controller.GetMethod(method, BindingFlags.Public | BindingFlags.Instance)!
            .GetCustomAttribute<PlaygroundPromptLimitAttribute>().ShouldNotBeNull();
    }

    [Test]
    public async Task Chat_OversizedPageContext_IsCutBeforeTheActionRuns()
    {
        _mode.IsActive.Returns(true);
        var request = new LLMRequest
        {
            Message = "hi",
            PageContext = new AssistantPageContext
            {
                CurrentRoute = new string('r', 1_000_000),
                SelectedClientIds = Enumerable.Range(0, 500).Select(_ => new string('c', 10_000)).ToList(),
            },
        };

        var (result, ran) = await Run(request);

        ran.ShouldBeTrue();
        result.ShouldBeNull();
        request.PageContext!.CurrentRoute!.Length.ShouldBe(PlaygroundHardeningConstants.PlaygroundMaxContextFieldChars);
        request.PageContext.SelectedClientIds!.Count.ShouldBe(PlaygroundHardeningConstants.PlaygroundMaxSelectedClientIds);
        request.PageContext.SelectedClientIds.ShouldAllBe(id => id.Length == PlaygroundHardeningConstants.PlaygroundMaxContextFieldChars);
    }

    [Test]
    public async Task FunctionCallAndBatch_OversizedPageContext_IsCut()
    {
        _mode.IsActive.Returns(true);
        var single = new LLMFunctionExecuteRequest { FunctionName = "f", PageContext = new AssistantPageContext { CurrentRoute = new string('r', 5000) } };
        var batchItem = new LLMFunctionExecuteRequest { FunctionName = "f", PageContext = new AssistantPageContext { SelectedGroupId = new string('g', 5000) } };

        await Run(single);
        await Run(new List<LLMFunctionExecuteRequest> { batchItem });

        single.PageContext!.CurrentRoute!.Length.ShouldBe(PlaygroundHardeningConstants.PlaygroundMaxContextFieldChars);
        batchItem.PageContext!.SelectedGroupId!.Length.ShouldBe(PlaygroundHardeningConstants.PlaygroundMaxContextFieldChars);
    }

    [Test]
    public async Task ModeOff_PageContextIsLeftUntouched()
    {
        _mode.IsActive.Returns(false);
        var route = new string('r', 5000);
        var request = new LLMRequest { Message = "hi", PageContext = new AssistantPageContext { CurrentRoute = route } };

        await Run(request);

        request.PageContext!.CurrentRoute.ShouldBe(route);
    }

    [Test]
    public async Task RegularPageContext_IsNotChanged()
    {
        _mode.IsActive.Returns(true);
        var guid = Guid.NewGuid().ToString();
        var request = new LLMRequest
        {
            Message = "hi",
            PageContext = new AssistantPageContext
            {
                CurrentRoute = "/workplace/schedule",
                SelectedGroupId = guid,
                SelectedClientIds = new List<string> { guid },
            },
        };

        await Run(request);

        request.PageContext!.CurrentRoute.ShouldBe("/workplace/schedule");
        request.PageContext.SelectedGroupId.ShouldBe(guid);
        request.PageContext.SelectedClientIds.ShouldBe(new[] { guid });
    }

    [Test]
    public void PromptTooLongMessage_NamesTheConfiguredLimit()
    {
        PlaygroundHardeningConstants.PromptTooLongMessage.ShouldContain(Limit.ToString());
    }
}
