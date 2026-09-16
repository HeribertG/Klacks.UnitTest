// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// DataAnnotations validation of LLMRequest.ConversationId: it must stay within
/// GracefulCorrectionDefaults.ConversationIdMaxLength, the varchar(128) column
/// AssistantLastActionRowConfiguration declares - otherwise an over-long id would throw a
/// DbUpdateException out of PersistentAssistantLastActionStore.Save on every tool-calling turn instead
/// of failing at the request boundary with a 400.
/// </summary>

using System.ComponentModel.DataAnnotations;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Domain.Constants;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Application.DTOs.Assistant;

[TestFixture]
public class LLMRequestValidationTests
{
    private static bool IsValid(LLMRequest request, out List<ValidationResult> results)
    {
        results = [];
        var context = new ValidationContext(request);
        return Validator.TryValidateObject(request, context, results, validateAllProperties: true);
    }

    [Test]
    public void ConversationId_AtTheLengthLimit_IsValid()
    {
        var request = new LLMRequest
        {
            Message = "hi",
            ConversationId = new string('c', GracefulCorrectionDefaults.ConversationIdMaxLength)
        };

        IsValid(request, out _).ShouldBeTrue();
    }

    [Test]
    public void ConversationId_LongerThanTheLimit_IsInvalid()
    {
        var request = new LLMRequest
        {
            Message = "hi",
            ConversationId = new string('c', GracefulCorrectionDefaults.ConversationIdMaxLength + 1)
        };

        IsValid(request, out var results).ShouldBeFalse();
        results.ShouldContain(r => r.MemberNames.Contains(nameof(LLMRequest.ConversationId)));
    }

    [Test]
    public void ConversationId_Null_IsValid()
    {
        var request = new LLMRequest
        {
            Message = "hi",
            ConversationId = null
        };

        IsValid(request, out _).ShouldBeTrue();
    }
}
