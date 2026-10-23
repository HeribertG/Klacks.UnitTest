// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for UpdateAddressSkill.ExecuteAsync, written before its long body was split into private
/// steps: the exact not-found, no-op, failed-put, missing re-read and mismatch texts, the changed-field order (fixed
/// field order, not parameter order), the exact success text and data, the order owner lookup -> company clock -> put,
/// and that text fields already applied to the loaded address stay applied when a later type or date check refuses.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateAddressSkillCharacterizationTests
{
    private static readonly Guid AddressId = Guid.Parse("11111111-1111-1111-1111-111111111111");
    private static readonly Guid ClientId = Guid.Parse("22222222-2222-2222-2222-222222222222");
    private static readonly DateTime Today = new(2026, 9, 12, 0, 0, 0, DateTimeKind.Utc);

    private IMediator _mediator = null!;
    private ICompanyClock _clock = null!;
    private IClientRepository _clientRepository = null!;
    private UpdateAddressSkill _skill = null!;
    private AddressResource _address = null!;
    private AddressResource? _sent;

    [SetUp]
    public void Setup()
    {
        _sent = null;
        _address = new AddressResource
        {
            Id = AddressId,
            ClientId = ClientId,
            Street = "Alte Strasse 5",
            Zip = "3000",
            City = "Bern",
            Country = "Schweiz",
            Type = AddressTypeEnum.Employee
        };

        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(_ => _address);
        _mediator.Send(Arg.Any<PutCommand<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                _sent = ci.Arg<PutCommand<AddressResource>>().Resource;
                return _sent;
            });
        _clock = Substitute.For<ICompanyClock>();
        _clock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(Today);
        _clientRepository = Substitute.For<IClientRepository>();
        _clientRepository.GetTypeAndDisplayNameAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns((ClientTypeAndDisplayName?)null);

        _skill = new UpdateAddressSkill(_mediator, _clock, _clientRepository);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    private static Dictionary<string, object> Params(params (string Key, object Value)[] values)
    {
        var parameters = new Dictionary<string, object> { ["addressId"] = AddressId.ToString() };
        foreach (var (key, value) in values)
        {
            parameters[key] = value;
        }

        return parameters;
    }

    [Test]
    public async Task UnknownAddress_ReturnsExactNotFound()
    {
        _mediator.Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns<AddressResource>(_ => throw new KeyNotFoundException());

        var result = await _skill.ExecuteAsync(Ctx(), Params(("city", "Thun")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Address '{AddressId}' not found.");
    }

    [Test]
    public async Task UnchangedValues_ReturnNoOp_WithoutPut()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(("city", "Bern"), ("type", 0)));

        result.Success.ShouldBeTrue();
        result.Message.ShouldBe("No fields supplied for update — address left unchanged.");
        JsonSerializer.Serialize(result.Data).ShouldBe("{\"AddressId\":\"" + AddressId + "\",\"ChangedFields\":[]}");
        await _mediator.DidNotReceive().Send(Arg.Any<PutCommand<AddressResource>>(), Arg.Any<CancellationToken>());
        await _clientRepository.DidNotReceive().GetTypeAndDisplayNameAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task PutWithoutResult_ReturnsError()
    {
        _mediator.Send(Arg.Any<PutCommand<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns((AddressResource?)null);

        var result = await _skill.ExecuteAsync(Ctx(), Params(("city", "Thun")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"Updating address '{AddressId}' failed.");
    }

    [Test]
    public async Task MissingReRead_ReturnsExactError()
    {
        var calls = 0;
        _mediator.Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(_ => calls++ == 0 ? _address : throw new KeyNotFoundException());

        var result = await _skill.ExecuteAsync(Ctx(), Params(("city", "Thun")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            $"Address '{AddressId}' was updated but could not be re-read from the database — treat the update as not persisted.");
    }

    [Test]
    public async Task MismatchingReRead_ReturnsExactError()
    {
        var calls = 0;
        var stale = new AddressResource
        {
            Id = AddressId,
            ClientId = ClientId,
            Street = "Alte Strasse 5",
            Zip = "9999",
            City = "Bern",
            Country = "Schweiz",
            Type = AddressTypeEnum.Employee
        };
        _mediator.Send(Arg.Any<GetQuery<AddressResource>>(), Arg.Any<CancellationToken>())
            .Returns(_ => calls++ == 0 ? _address : stale);

        var result = await _skill.ExecuteAsync(Ctx(), Params(("city", "Thun")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(
            $"Address '{AddressId}' was written but database verification found mismatching fields: zip, city. "
            + "The stored values may differ from the requested update — re-read the address before relying on it.");
    }

    [Test]
    public async Task SeveralFields_AreReportedInFixedOrder_WithExactSuccessData()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(
            ("validFrom", "2026-09-01"),
            ("type", 1),
            ("country", "Deutschland"),
            ("zip", "8001"),
            ("street", "Neue Gasse 1")));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldBe("Address updated (street, zip, country, type, validFrom) and confirmed in the database (verified).");
        JsonSerializer.Serialize(result.Data).ShouldBe(
            "{\"AddressId\":\"" + AddressId + "\","
            + "\"ChangedFields\":[\"street\",\"zip\",\"country\",\"type\",\"validFrom\"],"
            + "\"ClientId\":\"" + ClientId + "\",\"Street\":\"Neue Gasse 1\",\"Zip\":\"8001\",\"City\":\"Bern\","
            + "\"Country\":\"Deutschland\",\"Type\":1,\"ValidFrom\":\"2026-09-01T00:00:00Z\",\"Verified\":true}");
        _sent.ShouldBeSameAs(_address);
        Received.InOrder(() =>
        {
            _clientRepository.GetTypeAndDisplayNameAsync(ClientId, Arg.Any<CancellationToken>());
            _clock.GetTodayAsync(Arg.Any<CancellationToken>());
            _mediator.Send(Arg.Any<PutCommand<AddressResource>>(), Arg.Any<CancellationToken>());
        });
    }

    [Test]
    public async Task RejectedType_KeepsEarlierTextChangesOnTheLoadedAddress()
    {
        _clientRepository.GetTypeAndDisplayNameAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>())
            .Returns(new ClientTypeAndDisplayName(EntityTypeEnum.Employee, "Hans Muster"));

        var result = await _skill.ExecuteAsync(Ctx(), Params(("street", "Neue Gasse 1"), ("type", 1)));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(ClientAddressTypeRulesMessage());
        _address.Street.ShouldBe("Neue Gasse 1");
        _address.Type.ShouldBe(AddressTypeEnum.Employee);
        await _mediator.DidNotReceive().Send(Arg.Any<PutCommand<AddressResource>>(), Arg.Any<CancellationToken>());
        await _clock.DidNotReceive().GetTodayAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task InvalidValidFrom_KeepsEarlierChangesOnTheLoadedAddress()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(("city", "Thun"), ("validFrom", "irgendwann")));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe(SkillDateParser.InvalidDateMessageFor("validFrom", "irgendwann"));
        _address.City.ShouldBe("Thun");
        await _mediator.DidNotReceive().Send(Arg.Any<PutCommand<AddressResource>>(), Arg.Any<CancellationToken>());
    }

    private static string ClientAddressTypeRulesMessage() =>
        Klacks.Api.Application.Validation.Clients.ClientAddressTypeRules.DescribeViolation(
            EntityTypeEnum.Employee, AddressTypeEnum.Workplace);
}
