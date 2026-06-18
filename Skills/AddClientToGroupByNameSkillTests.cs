// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AddClientToGroupByNameSkill — the anti-hallucination guard: an unknown group
/// name is rejected with an error that lists the real available group names; the happy path adds
/// a GroupItem for a real group.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddClientToGroupByNameSkillTests
{
    private IClientRepository _clientRepository = null!;
    private IClientSearchRepository _searchRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private AddClientToGroupByNameSkill _skill = null!;

    private static readonly Guid ClientId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _skill = new AddClientToGroupByNameSkill(
            _clientRepository, _searchRepository, _groupRepository, _unitOfWork);

        _searchRepository.SearchAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                Items = new List<ClientSearchItem> { new() { Id = ClientId, FirstName = "Max", LastName = "Müller" } },
                TotalCount = 1
            });
        _clientRepository.Get(ClientId).Returns(new Client { Id = ClientId, FirstName = "Max", Name = "Müller" });

        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = Guid.NewGuid(), Name = "Verkauf" },
            new() { Id = Guid.NewGuid(), Name = "Logistik" }
        });
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    [Test]
    public async Task ReturnsError_ListingRealGroups_WhenGroupNameIsHallucinated()
    {
        var parameters = new Dictionary<string, object>
        {
            ["firstName"] = "Max",
            ["lastName"] = "Müller",
            ["groupName"] = "Administration"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Administration"));
        Assert.That(result.Message, Does.Contain("Verkauf"));
        Assert.That(result.Message, Does.Contain("Logistik"));
        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>());
    }

    [Test]
    public async Task AddsClientToGroup_WhenGroupExists()
    {
        var parameters = new Dictionary<string, object>
        {
            ["firstName"] = "Max",
            ["lastName"] = "Müller",
            ["groupName"] = "Verkauf"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        await _clientRepository.Received(1).Put(Arg.Is<Client>(c => c.GroupItems.Any(gi => !gi.IsDeleted)));
        await _unitOfWork.Received(1).CompleteAsync();
    }
}
