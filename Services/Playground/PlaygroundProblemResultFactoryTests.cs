// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests for PlaygroundProblemResultFactory: the three Playground refusals share one ProblemDetails shape with the
/// standard reason phrase as title, the matching status and the errorCode extension.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Presentation.Filters;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundProblemResultFactoryTests
{
    private static ProblemDetails Problem(ObjectResult result) => result.Value.ShouldBeOfType<ProblemDetails>();

    [Test]
    public void FeatureBlocked_Is403WithTheBlockedCode()
    {
        var result = PlaygroundProblemResultFactory.FeatureBlocked();

        result.StatusCode.ShouldBe(StatusCodes.Status403Forbidden);
        Problem(result).Title.ShouldBe("Forbidden");
        Problem(result).Detail.ShouldBe(PlaygroundHardeningConstants.FeatureBlockedMessage);
        Problem(result).Extensions[PlaygroundHardeningConstants.ErrorCodeExtension].ShouldBe(PlaygroundHardeningConstants.FeatureBlockedErrorCode);
    }

    [Test]
    public void PromptTooLong_Is400WithTheTooLongCode()
    {
        var result = PlaygroundProblemResultFactory.PromptTooLong();

        result.StatusCode.ShouldBe(StatusCodes.Status400BadRequest);
        Problem(result).Title.ShouldBe("Bad Request");
        Problem(result).Detail.ShouldBe(PlaygroundHardeningConstants.PromptTooLongMessage);
        Problem(result).Extensions[PlaygroundHardeningConstants.ErrorCodeExtension].ShouldBe(PlaygroundHardeningConstants.PromptTooLongErrorCode);
    }

    [Test]
    public void TokenLimitReached_Is429WithTheLimitCode()
    {
        var result = PlaygroundProblemResultFactory.TokenLimitReached();

        result.StatusCode.ShouldBe(StatusCodes.Status429TooManyRequests);
        Problem(result).Title.ShouldBe("Too Many Requests");
        Problem(result).Detail.ShouldBe(PlaygroundTokenLimitConstants.LimitReachedMessage);
        Problem(result).Extensions[PlaygroundHardeningConstants.ErrorCodeExtension].ShouldBe(PlaygroundTokenLimitConstants.ErrorCode);
    }

    [Test]
    public void Create_UsesTheReasonPhraseOfAnyStatus()
    {
        var result = PlaygroundProblemResultFactory.Create(StatusCodes.Status404NotFound, "detail", "CODE");

        result.StatusCode.ShouldBe(StatusCodes.Status404NotFound);
        Problem(result).Title.ShouldBe("Not Found");
        Problem(result).Status.ShouldBe(StatusCodes.Status404NotFound);
        Problem(result).Extensions[PlaygroundHardeningConstants.ErrorCodeExtension].ShouldBe("CODE");
    }
}
