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
    private ICompanyClock _companyClock = null!;
    private AddClientToGroupByNameSkill _skill = null!;

    private static readonly Guid ClientId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _searchRepository = Substitute.For<IClientSearchRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>())
            .Returns(new DateTime(2026, 6, 28, 0, 0, 0, DateTimeKind.Utc));
        _skill = new AddClientToGroupByNameSkill(
            _clientRepository, _searchRepository, _groupRepository, _unitOfWork, _companyClock);

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

    private static SkillExecutionContext Ctx(IReadOnlyList<Guid>? selection = null) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients" },
        SelectedEntityIds = selection
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
            ["groupName"] = "Verkauf",
            ["validFrom"] = "2026-05-01"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.True);
        await _clientRepository.Received(1).Put(Arg.Is<Client>(c => c.GroupItems.Any(gi => !gi.IsDeleted)));
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task AsksForStartDate_AndDoesNotPersist_WhenValidFromIsMissing()
    {
        var parameters = new Dictionary<string, object>
        {
            ["firstName"] = "Max",
            ["lastName"] = "Müller",
            ["groupName"] = "Verkauf"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("date"));
        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task OnAmbiguousName_TellsModelToFinishItself_NotOpenTheEditPage()
    {
        _searchRepository.SearchAsync(Arg.Any<string?>(), Arg.Any<string?>(), Arg.Any<EntityTypeEnum?>(), Arg.Any<Guid?>(), Arg.Any<int>(), Arg.Any<CancellationToken>())
            .Returns(new ClientSearchResult
            {
                Items = new List<ClientSearchItem>
                {
                    new() { Id = Guid.NewGuid(), FirstName = "Lya", LastName = "Ackermann", IdNumber = 1450 },
                    new() { Id = Guid.NewGuid(), FirstName = "Lya", LastName = "Ackermann", IdNumber = 1622 }
                },
                TotalCount = 2
            });

        var parameters = new Dictionary<string, object>
        {
            ["firstName"] = "Lya",
            ["lastName"] = "Ackermann",
            ["groupName"] = "Verkauf",
            ["validFrom"] = "2026-06-01"
        };

        var result = await _skill.ExecuteAsync(Ctx(), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("1450"));
        Assert.That(result.Message, Does.Contain("edit page"));
        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>());
    }

    [Test]
    public async Task RedirectsToBulkSkill_WhenNamedClientIsPartOfAnActiveSelection()
    {
        var parameters = new Dictionary<string, object>
        {
            ["firstName"] = "Max",
            ["lastName"] = "Müller",
            ["groupName"] = "Verkauf",
            ["validFrom"] = "2026-06-01"
        };

        var selection = new List<Guid> { ClientId, Guid.NewGuid(), Guid.NewGuid() };
        var result = await _skill.ExecuteAsync(Ctx(selection), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("add_selected_clients_to_group"));
        await _clientRepository.DidNotReceive().Put(Arg.Any<Client>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}
