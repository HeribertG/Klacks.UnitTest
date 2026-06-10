// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for list_countries: the skill sends ListQuery&lt;CountryResource&gt; and projects
/// id, abbreviation, prefix and multilingual name; an empty list yields a zero-count success.
/// </summary>

using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListCountriesSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    [Test]
    public async Task List_ReturnsCountries()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery<CountryResource>>(), Arg.Any<CancellationToken>())
            .Returns(new List<CountryResource>
            {
                new() { Id = Guid.NewGuid(), Abbreviation = "CH", Prefix = "+41", Name = new MultiLanguage { De = "Schweiz", En = "Switzerland" } },
                new() { Id = Guid.NewGuid(), Abbreviation = "DE", Prefix = "+49", Name = new MultiLanguage { De = "Deutschland", En = "Germany" } }
            });
        var skill = new ListCountriesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("2 countries");
        await mediator.Received(1).Send(
            Arg.Any<ListQuery<CountryResource>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_Empty_ReturnsZeroCount()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery<CountryResource>>(), Arg.Any<CancellationToken>())
            .Returns(new List<CountryResource>());
        var skill = new ListCountriesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("0 countries");
    }
}
