// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for DeleteClientSkill — not-found, verified happy path (message carries
/// "verified") and the rollback path when the client is still visible after the delete.
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
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.Arg<Func<Task<bool>>>()());
        _clientRepository.GetNoTracking(Arg.Any<Guid>()).Returns((Client?)null);
        _skill = new DeleteClientSkill(_clientRepository, _unitOfWork);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanDeleteClients" }
    };

    private static Client MakeClient(Guid id) => new()
    {
        Id = id,
        FirstName = "Max",
        Name = "Müller",
        Type = EntityTypeEnum.Employee
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
    public async Task SoftDeletesClient_AndConfirmsRemoval()
    {
        var id = Guid.NewGuid();
        _clientRepository.Get(id).Returns(MakeClient(id));
        var parameters = new Dictionary<string, object> { ["clientId"] = id.ToString() };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True, result.Message);
        Assert.That(result.Message, Does.Contain("verified"));
        await _clientRepository.Received(1).Delete(id);
        await _unitOfWork.Received(1).CompleteAsync();
        await _clientRepository.Received(1).GetNoTracking(id);
    }

    [Test]
    public async Task ReturnsError_WhenClientStillVisibleAfterDelete()
    {
        var id = Guid.NewGuid();
        _clientRepository.Get(id).Returns(MakeClient(id));
        _clientRepository.GetNoTracking(id).Returns(MakeClient(id));
        var parameters = new Dictionary<string, object> { ["clientId"] = id.ToString() };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("verification failed"));
        await _clientRepository.Received(1).Delete(id);
    }
}
