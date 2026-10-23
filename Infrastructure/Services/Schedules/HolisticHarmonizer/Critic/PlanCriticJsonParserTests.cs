// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Services.Schedules.HolisticHarmonizer.Critic;
using Klacks.Api.Infrastructure.Services.Schedules.HolisticHarmonizer.Critic;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Services.Schedules.HolisticHarmonizer.Critic;

/// <summary>
/// The parser reads the critic answer leniently; truth is decided later by the verifier, so a malformed field must
/// never throw or drop the whole answer.
/// </summary>
[TestFixture]
public sealed class PlanCriticJsonParserTests
{
    [Test]
    public void AnswerInCodeFenceWithProse_IsParsed()
    {
        const string raw = "Here you go:\n```json\n{\"findings\":[{\"pattern\":\"night_then_early\",\"severity\":\"HIGH\","
            + "\"description\":\"r00\",\"cells\":[{\"row\":0,\"day\":0,\"symbol\":\"N\"}]}]}\n```";

        var findings = PlanCriticJsonParser.TryParse(raw, out var error);

        error.ShouldBeNull();
        var finding = findings.ShouldHaveSingleItem();
        finding.Pattern.ShouldBe("night_then_early");
        finding.Severity.ShouldBe(PlanCriticSeverity.High);
        finding.Cells.ShouldHaveSingleItem().ShouldBe(new PlanCriticCell(0, 0, "N"));
    }

    [TestCase("weird")]
    [TestCase("")]
    [TestCase("7")]
    public void UnknownSeverity_FallsBackToMedium(string severity)
    {
        var raw = "{\"findings\":[{\"pattern\":\"p\",\"severity\":\"" + severity + "\",\"cells\":[]}]}";

        PlanCriticJsonParser.TryParse(raw, out _).Single().Severity.ShouldBe(PlanCriticSeverity.Medium);
    }

    [Test]
    public void CellWithoutIntegerCoordinates_IsDropped()
    {
        const string raw = "{\"findings\":[{\"pattern\":\"p\",\"cells\":[{\"row\":\"x\",\"day\":0},{\"row\":1,\"day\":2,\"symbol\":\"_\"}]}]}";

        PlanCriticJsonParser.TryParse(raw, out _).Single().Cells.ShouldHaveSingleItem().Row.ShouldBe(1);
    }

    [Test]
    public void EmptyFindings_IsASuccessfulEmptyReview()
    {
        var findings = PlanCriticJsonParser.TryParse("{\"findings\":[]}", out var error);

        findings.ShouldBeEmpty();
        error.ShouldBeNull();
    }

    [TestCase("no json at all")]
    [TestCase("{\"something\":1}")]
    [TestCase("{\"findings\":{}}")]
    public void AnswerWithoutFindingsArray_IsAnError(string raw)
    {
        PlanCriticJsonParser.TryParse(raw, out var error).ShouldBeEmpty();
        error.ShouldNotBeNull();
    }

    [Test]
    public void Prompt_AsksForTheRunLanguageAndTheFindingCap()
    {
        var request = new PlanCriticRequest("m", [1], "grid", "de", 7);

        var system = PlanCriticPromptBuilder.BuildSystemPrompt(request);

        system.ShouldContain("\"de\"");
        system.ShouldContain("At most 7 findings");
        system.ShouldContain("{\"findings\":[]}");
        PlanCriticPromptBuilder.BuildUserMessage(request).ShouldContain("grid");
    }
}
