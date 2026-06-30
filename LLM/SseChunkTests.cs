// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for SseChunk factory methods — verifies that the Metadata factory
/// propagates NavigateToTarget to the Target field on the chunk.
/// </summary>

using Klacks.Api.Domain.Models.Assistant;
using Shouldly;

namespace Klacks.UnitTest.LLM;

[TestFixture]
public class SseChunkTests
{
    private static LLMResponse BuildResponse(string? navigateTo = null, string? navigateToTarget = null) =>
        new()
        {
            Message = "Done",
            NavigateTo = navigateTo,
            NavigateToTarget = navigateToTarget
        };

    [Test]
    public void Metadata_WithNavigateToTarget_SetsTargetOnChunk()
    {
        var response = BuildResponse("/workplace/settings", "macros");

        var chunk = SseChunk.Metadata(response);

        chunk.NavigateTo.ShouldBe("/workplace/settings");
        chunk.Target.ShouldBe("macros");
    }

    [Test]
    public void Metadata_WithNullNavigateToTarget_TargetIsNull()
    {
        var response = BuildResponse("/workplace/settings", null);

        var chunk = SseChunk.Metadata(response);

        chunk.NavigateTo.ShouldBe("/workplace/settings");
        chunk.Target.ShouldBeNull();
    }

    [Test]
    public void Metadata_WithNoNavigation_BothNavigateFieldsAreNull()
    {
        var response = BuildResponse();

        var chunk = SseChunk.Metadata(response);

        chunk.NavigateTo.ShouldBeNull();
        chunk.Target.ShouldBeNull();
    }

    [Test]
    public void Metadata_TypeIsMetadata()
    {
        var chunk = SseChunk.Metadata(BuildResponse());

        chunk.Type.ShouldBe(SseChunkType.Metadata);
    }
}
