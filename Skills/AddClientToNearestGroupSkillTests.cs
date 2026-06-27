// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AddClientToNearestGroupSkill: with a start date the client is added to the
/// geographically nearest group, but when no start date is supplied the skill asks the user for one and
/// does not persist — the plannability boundary (ValidFrom) must never silently default to today.
/// </summary>

using System.Collections.ObjectModel;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddClientToNearestGroupSkillTests
{
    private IClientRepository _clientRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IGroupItemRepository _groupItemRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private AddClientToNearestGroupSkill _skill = null!;

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid BernGroupId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupItemRepository = Substitute.For<IGroupItemRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _skill = new AddClientToNearestGroupSkill(
            _clientRepository, _groupRepository, _groupItemRepository, _unitOfWork);

        _clientRepository.Get(ClientId).Returns(new Client
        {
            Id = ClientId,
            FirstName = "Max",
            Name = "Müller",
            Addresses = new Collection<Address>
            {
                new() { Latitude = 46.948, Longitude = 7.447 }
            },
            GroupItems = new Collection<GroupItem>()
        });

        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = BernGroupId, Name = "Bern", Latitude = 46.948, Longitude = 7.447 }
        });
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients", "CanViewGroups" }
    };

    [Test]
    public async Task AddsClientToNearestGroup_WhenValidFromIsGiven()
    {
        var parameters = new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["validFrom"] = "2026-05-01"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        await _groupItemRepository.Received(1).Add(
            Arg.Is<GroupItem>(gi => gi.GroupId == BernGroupId
                && gi.ValidFrom == new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task AsksForStartDate_AndDoesNotPersist_WhenValidFromIsMissing()
    {
        var parameters = new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString()
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("date"));
        await _groupItemRepository.DidNotReceive().Add(Arg.Any<GroupItem>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}
