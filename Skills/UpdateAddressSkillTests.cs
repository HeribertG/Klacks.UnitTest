// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for update_address: loads via GetQuery&lt;AddressResource&gt;, mutates only supplied
/// fields and dispatches PutCommand&lt;AddressResource&gt;; an unknown id yields an error.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateAddressSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    private static AddressResource Address(Guid id) => new()
    {
        Id = id,
        ClientId = Guid.NewGuid(),
        Street = "Alte Strasse 5",
        Zip = "3000",
        City = "Bern",
        Country = "Schweiz",
        Type = AddressTypeEnum.Employee
    };

    [Test]
    public async Task UpdateCityAndZip_DispatchesPutCommand_WithMergedValues()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(Address(id));
        mediator.Send(Arg.Any<PutCommand<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<AddressResource>)ci[0]).Resource);
        var skill = new UpdateAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["addressId"] = id.ToString(),
            ["city"] = "Zürich",
            ["zip"] = "8001"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<AddressResource>>(c =>
                c.Resource.Id == id &&
                c.Resource.City == "Zürich" &&
                c.Resource.Zip == "8001" &&
                c.Resource.Street == "Alte Strasse 5"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownId_ReturnsError_NoPut()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns<AddressResource>(_ => throw new KeyNotFoundException());
        var skill = new UpdateAddressSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["addressId"] = Guid.NewGuid().ToString(),
            ["city"] = "Zürich"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<AddressResource>>(), Arg.Any<CancellationToken>());
    }
}
