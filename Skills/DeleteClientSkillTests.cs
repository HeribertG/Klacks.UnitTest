// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for DeleteClientSkill — not-found + happy path.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class DeleteClientSkillTests
{
    private IClientRepository _clientRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private DeleteClientSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _skill = new DeleteClientSkill(_clientRepository, _unitOfWork);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanDeleteClients" }
    };

    [Test]
    public async Task ReturnsError_WhenClientNotFound()
    {
        var id = Guid.NewGuid();
        _clientRepository.Get(id).Returns((Client?)null);
        var parameters = new Dictionary<string, object> { ["clientId"] = id.ToString() };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        await _clientRepository.DidNotReceive().Delete(Arg.Any<Guid>());
    }

    [Test]
    public async Task SoftDeletesClient_WhenFound()
    {
        var id = Guid.NewGuid();
        _clientRepository.Get(id).Returns(new Client
        {
            Id = id,
            FirstName = "Max",
            Name = "Müller",
            Type = EntityTypeEnum.Employee
        });
        var parameters = new Dictionary<string, object> { ["clientId"] = id.ToString() };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        await _clientRepository.Received(1).Delete(id);
        await _unitOfWork.Received(1).CompleteAsync();
    }
}
