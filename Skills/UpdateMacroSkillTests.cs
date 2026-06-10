// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for update_macro: the skill loads the macro by id via GetQuery, merges only the
/// provided fields (name, script, description) onto the current resource and dispatches a
/// PutCommand with the merged result.
/// </summary>

using Klacks.Api.Application.Commands.Settings.Macros;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Queries.Settings.Macros;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateMacroSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditSettings" }
    };

    private static MacroResource Existing(Guid id) => new()
    {
        Id = id,
        Name = "Sunday rate",
        Content = "OUTPUT 1, 0",
        Type = 1,
        Description = new MultiLanguage { De = "Alt" }
    };

    [Test]
    public async Task UpdateMacro_MergesProvidedFields_DispatchesPutCommand()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>()).Returns(Existing(id));
        mediator.Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand)ci[0]).model);
        var skill = new UpdateMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = id.ToString(),
            ["name"] = "Holiday rate",
            ["script"] = "OUTPUT 2, 0"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand>(c =>
                c.model.Id == id &&
                c.model.Name == "Holiday rate" &&
                c.model.Content == "OUTPUT 2, 0"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateMacro_OnlyScript_KeepsExistingName()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>()).Returns(Existing(id));
        mediator.Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand)ci[0]).model);
        var skill = new UpdateMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = id.ToString(),
            ["script"] = "OUTPUT 3, 0"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand>(c =>
                c.model.Name == "Sunday rate" &&
                c.model.Content == "OUTPUT 3, 0"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateMacro_DescriptionApplied_ToAllCoreLanguages()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>()).Returns(Existing(id));
        mediator.Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand)ci[0]).model);
        var skill = new UpdateMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = id.ToString(),
            ["description"] = "New text"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand>(c =>
                c.model.Description.De == "New text" &&
                c.model.Description.En == "New text" &&
                c.model.Description.Fr == "New text" &&
                c.model.Description.It == "New text"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateMacro_MissingMacroId_ReturnsError_NoDispatch()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new UpdateMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Holiday rate"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateMacro_NoFieldsToUpdate_ReturnsError_NoDispatch()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new UpdateMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>());
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateMacro_UnknownMacro_ReturnsError_NoPut()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>()).Returns((MacroResource?)null);
        var skill = new UpdateMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = Guid.NewGuid().ToString(),
            ["name"] = "Holiday rate"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>());
    }
}
