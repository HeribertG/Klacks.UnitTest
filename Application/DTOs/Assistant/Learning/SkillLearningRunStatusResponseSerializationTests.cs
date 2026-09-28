// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the wire contract of the trigger field: the export and weekly scripts read run-status as JSON, and a
/// bare enum would serialize as its underlying int (0/1), which is meaningless once a third trigger is added
/// or the enum order changes. camelCase mirrors Program.cs PropertyNamingPolicy.
/// </summary>
namespace Klacks.UnitTest.Application.DTOs.Assistant.Learning;

using System.Text.Json;
using Klacks.Api.Application.DTOs.Assistant.Learning;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using NUnit.Framework;
using Shouldly;

[TestFixture]
public class SkillLearningRunStatusResponseSerializationTests
{
    private static readonly JsonSerializerOptions Options = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };

    [Test]
    public void Serialize_TheTrigger_IsAStringNotANumber()
    {
        var response = new SkillLearningRunStatusResponse(
            true, DateTime.UtcNow, null, null, null, null, SkillLearningRunTrigger.Manual);

        var json = JsonSerializer.Serialize(response, Options);

        json.ShouldContain("\"lastTrigger\":\"Manual\"");
    }

    [Test]
    public void Serialize_ANullTrigger_IsNull()
    {
        var response = new SkillLearningRunStatusResponse(false, null, null, null, null, null, null);

        var json = JsonSerializer.Serialize(response, Options);

        json.ShouldContain("\"lastTrigger\":null");
    }

    // The weekly pipeline script reads these two fields off the plain JSON to decide whether the optimizer
    // produced nothing this run; camelCase mirrors Program.cs PropertyNamingPolicy just like every other field.
    [Test]
    public void Serialize_TheOptimizerCounters_AreCamelCase()
    {
        var summary = new SkillLearningRunSummary(0, 0, 0, 0, 0, 0, 0, 0, 3, 2);
        var response = new SkillLearningRunStatusResponse(false, null, null, true, null, summary, null);

        var json = JsonSerializer.Serialize(response, Options);

        json.ShouldContain("\"optimizerAttempts\":3");
        json.ShouldContain("\"optimizerFailures\":2");
    }
}
