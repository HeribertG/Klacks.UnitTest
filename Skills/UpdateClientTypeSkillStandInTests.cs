// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// UpdateClientTypeSkill and the stand-in: no client is changed into a stand-in (by name or by number), a stand-in is
/// not changed into another type, and in every refused case no update is sent. A change between master data types
/// still goes through.
/// </summary>

using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Services.Assistant;
using Klacks.UnitTest.Infrastructure.SelfApi;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateClientTypeSkillStandInTests
{
    private static readonly Guid EmployeeId = Guid.NewGuid();
    private static readonly Guid StandInId = Guid.NewGuid();

    private IClientRepository _clientRepository = null!;
    private FakeSelfApi _api = null!;
    private UpdateClientTypeSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _api = new FakeSelfApi();
        _api.Respond(HttpMethod.Put, "api/backend/Clients", new ClientResource());
        _clientRepository.Get(EmployeeId).Returns(new Client
        {
            Id = EmployeeId, FirstName = "Anna", Name = "Muster", Type = EntityTypeEnum.Employee
        });
        _clientRepository.Get(StandInId).Returns(new Client
        {
            Id = StandInId, FirstName = string.Empty, Name = "Fair 1", Type = EntityTypeEnum.StandIn
        });
        _skill = new UpdateClientTypeSkill(
            _clientRepository, SkillClientVisibility.AllVisible(), Substitute.For<IClientSearchRepository>(), new ClientMapper(),
            _api.Client, new SelfApiRouteResolver(), Substitute.For<ILogger<UpdateClientTypeSkill>>());
    }

    [TearDown]
    public void TearDown() => _api.Dispose();

    [TestCase("StandIn")]
    [TestCase("3")]
    [TestCase("Employee,StandIn")]
    public async Task ToStandIn_IsRefused(string targetType)
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(EmployeeId, targetType));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("is not a valid client type");
        result.Message.ShouldContain("Employee, ExternEmp, Customer");
        _api.Calls.ShouldBeEmpty();
    }

    [TestCase("Employee")]
    [TestCase("ExternEmp")]
    [TestCase("Customer")]
    public async Task FromStandIn_IsRefused(string targetType)
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(StandInId, targetType));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("stand-in");
        _api.Calls.ShouldBeEmpty();
    }

    [Test]
    public async Task EmployeeToExternEmp_IsStillChanged()
    {
        var result = await _skill.ExecuteAsync(Ctx(), Params(EmployeeId, "externemp"));

        result.Success.ShouldBeTrue(result.Message);
        _api.SingleCall.Method.ShouldBe(HttpMethod.Put);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients" },
        AccessToken = new BearerToken("caller-jwt")
    };

    private static Dictionary<string, object> Params(Guid clientId, string targetType) => new()
    {
        ["clientId"] = clientId.ToString(),
        ["targetType"] = targetType
    };
}
