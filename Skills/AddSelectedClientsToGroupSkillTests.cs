// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AddSelectedClientsToGroupSkill: an empty UI selection is rejected before any work, an
/// unknown group name is rejected while listing the real groups, the preview path sends Apply=false with
/// the selection taken from the execution context (not a parameter), and an apply flag arriving as a
/// JsonElement (the real tool-call shape) is correctly read as true.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands.Groups;
using Klacks.Api.Application.DTOs.Groups;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddSelectedClientsToGroupSkillTests
{
    private IGroupRepository _groupRepository = null!;
    private IMediator _mediator = null!;
    private AddSelectedClientsToGroupSkill _skill = null!;

    private static readonly Guid BernGroupId = Guid.NewGuid();
    private static readonly Guid SelectedId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _groupRepository = Substitute.For<IGroupRepository>();
        _mediator = Substitute.For<IMediator>();
        _skill = new AddSelectedClientsToGroupSkill(_groupRepository, _mediator);

        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = BernGroupId, Name = "Bern" },
            new() { Id = Guid.NewGuid(), Name = "Zürich" }
        });

        _mediator.Send(Arg.Any<AddSelectedClientsToGroupCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci =>
            {
                var cmd = ci.Arg<AddSelectedClientsToGroupCommand>();
                return Task.FromResult(new AddSelectedClientsToGroupResult(
                    Applied: cmd.Apply,
                    GroupName: cmd.GroupName,
                    RequestedCount: cmd.SelectedClientIds.Count,
                    FoundCount: cmd.SelectedClientIds.Count,
                    NotFoundCount: 0,
                    EligibleCount: 1,
                    AddedCount: cmd.Apply ? 1 : 0,
                    VerifiedCount: cmd.Apply ? 1 : 0,
                    AlreadyMemberCount: 0,
                    Clients: new List<ClientSearchItem> { new() { Id = SelectedId, FirstName = "Max", LastName = "Müller" } }));
            });
    }

    private static SkillExecutionContext Ctx(IReadOnlyList<Guid>? selection) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditClients", "CanViewGroups" },
        SelectedEntityIds = selection
    };

    [Test]
    public async Task ReturnsError_WhenNothingIsSelected()
    {
        var parameters = new Dictionary<string, object> { ["groupName"] = "Bern" };

        var result = await _skill.ExecuteAsync(Ctx(selection: null), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("selected"));
        await _mediator.DidNotReceive().Send(Arg.Any<AddSelectedClientsToGroupCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_ListingRealGroups_WhenGroupNameIsHallucinated()
    {
        var parameters = new Dictionary<string, object> { ["groupName"] = "Administration" };

        var result = await _skill.ExecuteAsync(Ctx(new[] { SelectedId }), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("Bern"));
        Assert.That(result.Message, Does.Contain("Zürich"));
        await _mediator.DidNotReceive().Send(Arg.Any<AddSelectedClientsToGroupCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Preview_SendsApplyFalse_WithSelectionFromContext()
    {
        var parameters = new Dictionary<string, object> { ["groupName"] = "Bern" };

        var result = await _skill.ExecuteAsync(Ctx(new[] { SelectedId }), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("Preview"));
        await _mediator.Received(1).Send(
            Arg.Is<AddSelectedClientsToGroupCommand>(c =>
                c.GroupId == BernGroupId && !c.Apply
                && c.SelectedClientIds.Count == 1 && c.SelectedClientIds[0] == SelectedId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExactGroupNameWins_WhenALongerGroupAlsoContainsIt()
    {
        _groupRepository.List().Returns(new List<Group>
        {
            new() { Id = Guid.NewGuid(), Name = "Bern-wöchentlich" },
            new() { Id = BernGroupId, Name = "Bern" }
        });
        var parameters = new Dictionary<string, object> { ["groupName"] = "Bern" };

        var result = await _skill.ExecuteAsync(Ctx(new[] { SelectedId }), parameters);

        Assert.That(result.Success, Is.True);
        await _mediator.Received(1).Send(
            Arg.Is<AddSelectedClientsToGroupCommand>(c => c.GroupId == BernGroupId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Preview_PassesParsedValidFrom_FromParameters()
    {
        var parameters = new Dictionary<string, object>
        {
            ["groupName"] = "Bern",
            ["validFrom"] = "2026-05-01"
        };

        await _skill.ExecuteAsync(Ctx(new[] { SelectedId }), parameters);

        await _mediator.Received(1).Send(
            Arg.Is<AddSelectedClientsToGroupCommand>(c =>
                c.ValidFrom == new DateTime(2026, 5, 1, 0, 0, 0, DateTimeKind.Utc)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ReturnsError_WhenValidFromIsUnparseable_AndDoesNotPersist()
    {
        var parameters = new Dictionary<string, object>
        {
            ["groupName"] = "Bern",
            ["validFrom"] = "irgendwann bald"
        };

        var result = await _skill.ExecuteAsync(Ctx(new[] { SelectedId }), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("date"));
        await _mediator.DidNotReceive().Send(Arg.Any<AddSelectedClientsToGroupCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Apply_AsJsonElement_IsReadAsTrue()
    {
        var parameters = new Dictionary<string, object>
        {
            ["groupName"] = "Bern",
            ["apply"] = JsonSerializer.SerializeToElement(true),
            ["validFrom"] = "2026-05-01"
        };

        var result = await _skill.ExecuteAsync(Ctx(new[] { SelectedId }), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("Added"));
        await _mediator.Received(1).Send(
            Arg.Is<AddSelectedClientsToGroupCommand>(c => c.Apply),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Apply_WithoutValidFrom_AsksForStartDate_AndDoesNotPersist()
    {
        var parameters = new Dictionary<string, object>
        {
            ["groupName"] = "Bern",
            ["apply"] = JsonSerializer.SerializeToElement(true)
        };

        var result = await _skill.ExecuteAsync(Ctx(new[] { SelectedId }), parameters);

        Assert.That(result.Success, Is.False);
        Assert.That(result.Message, Does.Contain("date"));
        await _mediator.DidNotReceive().Send(Arg.Any<AddSelectedClientsToGroupCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Preview_WithoutValidFrom_AsksForStartDate_InTheSameTurn()
    {
        var parameters = new Dictionary<string, object> { ["groupName"] = "Bern" };

        var result = await _skill.ExecuteAsync(Ctx(new[] { SelectedId }), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("Preview"));
        Assert.That(result.Message, Does.Contain("validFrom"));
        await _mediator.Received(1).Send(
            Arg.Is<AddSelectedClientsToGroupCommand>(c => !c.Apply),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Preview_InstructsApplyTrue_AndForbidsPerNameAdds()
    {
        var parameters = new Dictionary<string, object>
        {
            ["groupName"] = "Bern",
            ["validFrom"] = "2026-05-01"
        };

        var result = await _skill.ExecuteAsync(Ctx(new[] { SelectedId }), parameters);

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("apply=true"));
        Assert.That(result.Message, Does.Contain("by name"));
    }
}
