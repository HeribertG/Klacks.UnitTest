// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for list_states: the skill sends ListQuery&lt;StateResource&gt;, optionally filters
/// by country abbreviation (case-insensitive) and errors when the country has no states.
/// </summary>

using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListStatesSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    private static List<StateResource> SampleStates() => new()
    {
        new() { Id = Guid.NewGuid(), Abbreviation = "BE", CountryPrefix = "CH", Name = new MultiLanguage { De = "Bern", En = "Bern" } },
        new() { Id = Guid.NewGuid(), Abbreviation = "ZH", CountryPrefix = "CH", Name = new MultiLanguage { De = "Zürich", En = "Zurich" } },
        new() { Id = Guid.NewGuid(), Abbreviation = "BY", CountryPrefix = "DE", Name = new MultiLanguage { De = "Bayern", En = "Bavaria" } }
    };

    [Test]
    public async Task List_WithoutCountry_ReturnsAllStates()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery<StateResource>>(), Arg.Any<CancellationToken>())
            .Returns(SampleStates());
        var skill = new ListStatesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("3 states/cantons");
        await mediator.Received(1).Send(
            Arg.Any<ListQuery<StateResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_WithCountry_FiltersCaseInsensitive()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery<StateResource>>(), Arg.Any<CancellationToken>())
            .Returns(SampleStates());
        var skill = new ListStatesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["country"] = "ch" });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("2 states/cantons");
        result.Message.ShouldContain("'CH'");
    }

    [Test]
    public async Task List_WithUnknownCountry_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery<StateResource>>(), Arg.Any<CancellationToken>())
            .Returns(SampleStates());
        var skill = new ListStatesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["country"] = "XX" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No states found for country 'XX'");
    }
}
