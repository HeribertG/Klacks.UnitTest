// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the single-pair shift-preference upsert: the handler updates the existing
/// (client, shift) preference in place (never wipes the client's others) or adds one; the skill
/// validates the preference value and dispatches.
/// </summary>

using Klacks.Api.Application.Commands.ClientShiftPreferences;
using Klacks.Api.Application.Handlers.ClientShiftPreferences;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SetShiftPreferencesTests
{
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid ShiftId = Guid.NewGuid();

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanEditShifts" }
    };

    [Test]
    public async Task Handler_NoExisting_Adds()
    {
        var repo = Substitute.For<IClientShiftPreferenceRepository>();
        repo.GetByClientAndShiftAsync(ClientId, ShiftId, Arg.Any<CancellationToken>()).Returns((ClientShiftPreference?)null);
        var uow = Substitute.For<IUnitOfWork>();
        var handler = new SetShiftPreferenceCommandHandler(repo, uow);

        await handler.Handle(new SetShiftPreferenceCommand(ClientId, ShiftId, ShiftPreferenceType.Preferred), CancellationToken.None);

        await repo.Received(1).Add(Arg.Is<ClientShiftPreference>(p =>
            p.ClientId == ClientId && p.ShiftId == ShiftId && p.PreferenceType == ShiftPreferenceType.Preferred && p.AnalyseToken == null));
        await uow.Received(1).CompleteAsync();
    }

    [Test]
    public async Task Handler_Existing_UpdatesInPlace_NoAdd()
    {
        var existing = new ClientShiftPreference
        {
            Id = Guid.NewGuid(), ClientId = ClientId, ShiftId = ShiftId, PreferenceType = ShiftPreferenceType.Preferred
        };
        var repo = Substitute.For<IClientShiftPreferenceRepository>();
        repo.GetByClientAndShiftAsync(ClientId, ShiftId, Arg.Any<CancellationToken>()).Returns(existing);
        var uow = Substitute.For<IUnitOfWork>();
        var handler = new SetShiftPreferenceCommandHandler(repo, uow);

        var id = await handler.Handle(new SetShiftPreferenceCommand(ClientId, ShiftId, ShiftPreferenceType.Blacklist), CancellationToken.None);

        id.ShouldBe(existing.Id);
        existing.PreferenceType.ShouldBe(ShiftPreferenceType.Blacklist);
        await repo.DidNotReceive().Add(Arg.Any<ClientShiftPreference>());
    }

    [Test]
    public async Task Skill_Valid_Dispatches()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<SetShiftPreferenceCommand>(), Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        var skill = new SetShiftPreferencesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["shiftId"] = ShiftId.ToString(),
            ["preference"] = "blacklist"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<SetShiftPreferenceCommand>(c => c.PreferenceType == ShiftPreferenceType.Blacklist),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Skill_InvalidPreference_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new SetShiftPreferencesSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["shiftId"] = ShiftId.ToString(),
            ["preference"] = "maybe"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<SetShiftPreferenceCommand>(), Arg.Any<CancellationToken>());
    }
}
