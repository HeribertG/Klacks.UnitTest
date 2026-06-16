// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for create_address: dispatches PostCommand&lt;AddressResource&gt; with the supplied client
/// id and address fields; a null result (creation failure) yields an error.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CreateAddressSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    [Test]
    public async Task CreateAddress_DispatchesPostCommand_WithClientAndFields()
    {
        var clientId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<PostCommand<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PostCommand<AddressResource>)ci[0]).Resource);
        var skill = new CreateAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = clientId.ToString(),
            ["street"] = "Bahnhofstrasse 1",
            ["zip"] = "8001",
            ["city"] = "Zürich"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PostCommand<AddressResource>>(c =>
                c.Resource.ClientId == clientId &&
                c.Resource.Street == "Bahnhofstrasse 1" &&
                c.Resource.Zip == "8001" &&
                c.Resource.City == "Zürich"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateAddress_NullResult_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<PostCommand<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns((AddressResource?)null);
        var skill = new CreateAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = Guid.NewGuid().ToString(),
            ["city"] = "Bern"
        });

        result.Success.ShouldBeFalse();
    }
}
