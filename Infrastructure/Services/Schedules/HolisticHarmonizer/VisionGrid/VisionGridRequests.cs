// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Infrastructure.Services.Schedules.HolisticHarmonizer;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.HolisticHarmonizer.VisionGrid;

/// <summary>
/// Builds the grid vision request on top of the production capability probe request, so it keeps the same
/// parameters (temperature 0, thinking off, output headroom) and only swaps the prompts and the image.
/// </summary>
public static class VisionGridRequests
{
    private const string SystemPrompt =
        "You are a deterministic vision verifier for the Klacks Holistic Harmonizer (Wizard 3).\n" +
        "The attached PNG is a staff schedule. Rows are employees, labelled with their initials in the left header. " +
        "Columns are days, numbered in the top header.\n" +
        "Every cell shows one bold letter: E (yellow), L (orange), N (dark blue), O (grey) or B (red hatched). " +
        "A white cell without a letter is free. Asked cells may be ringed in magenta with a numbered badge.\n" +
        "For each asked cell give its letter, or \"-\" for a free cell, in the order asked.\n" +
        "Reply with ONE JSON object and nothing else: {\"answers\":[\"...\",\"...\",\"...\"]}.\n" +
        "No prose, no markdown, no code fences, no commentary.\n" +
        "If you cannot see or process the image, reply with {\"answers\":[]}.";

    public static LLMProviderRequest Create(LLMModel model, VisionGridChallenge challenge)
    {
        var request = HolisticHarmonizerProbeRequests.Capability(model, challenge.Png);
        request.Message = challenge.UserMessage;
        request.SystemPrompt = SystemPrompt;
        return request;
    }
}
