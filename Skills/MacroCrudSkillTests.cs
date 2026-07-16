// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the macro CRUD skills: create_macro dispatches a PostCommand with the script as
/// content, list_macros projects and filters the ListQuery result, and delete_macro resolves a
/// macro by id or unambiguous name before dispatching a DeleteCommand.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands.Settings.Macros;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Queries.Settings.Macros;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class MacroCrudSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditSettings" }
    };

    [Test]
    public async Task CreateMacro_DispatchesPostCommand_WithScriptAsContent()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => new MacroResource
            {
                Id = Guid.NewGuid(),
                Name = ((PostCommand)ci[0]).model.Name,
                Content = ((PostCommand)ci[0]).model.Content
            });
        var skill = new CreateMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "MyMacro",
            ["script"] = "OUTPUT 1, 0"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PostCommand>(c =>
                c.model.Name == "MyMacro" &&
                c.model.Content == "OUTPUT 1, 0" &&
                c.model.Type == (int)MacroFunctionEnum.Custom &&
                c.model.Description != null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateMacro_MissingScript_ReturnsError_NoDispatch()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new CreateMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "MyMacro"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateMacro_PostRejectsScript_ReturnsValidationErrorMessage()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>())
            .Returns<MacroResource?>(_ => throw new InvalidRequestException("compile error: bad script"));
        var skill = new CreateMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "MyMacro",
            ["script"] = "DIM 123abc"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("compile error: bad script");
    }

    [Test]
    public async Task ListMacros_FiltersBySearchTerm_AndProjects()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource>
            {
                new() { Id = Guid.NewGuid(), Name = "Sunday rate", Type = 1 },
                new() { Id = Guid.NewGuid(), Name = "Night rate", Type = 1 }
            }.AsEnumerable());
        var skill = new ListMacrosSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["searchTerm"] = "sunday"
        });

        result.Success.ShouldBeTrue();
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("Count").GetInt32().ShouldBe(1);
        data.GetProperty("Macros")[0].GetProperty("Name").GetString().ShouldBe("Sunday rate");
    }

    [Test]
    public async Task DeleteMacro_ByName_ResolvesAndDispatchesDeleteCommand()
    {
        var targetId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource>
            {
                new() { Id = targetId, Name = "Sunday rate" },
                new() { Id = Guid.NewGuid(), Name = "Night rate" }
            }.AsEnumerable());
        mediator.Send(Arg.Any<DeleteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new MacroResource { Id = targetId, Name = "Sunday rate" });
        var skill = new DeleteMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroName"] = "Sunday rate"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<DeleteCommand>(c => c.Id == targetId), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteMacro_AmbiguousName_ReturnsError_NoDelete()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource>
            {
                new() { Id = Guid.NewGuid(), Name = "Rate weekend" },
                new() { Id = Guid.NewGuid(), Name = "Rate holiday" }
            }.AsEnumerable());
        var skill = new DeleteMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroName"] = "Rate"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<DeleteCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteMacro_UnknownName_ReturnsError_NoDelete()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource>().AsEnumerable());
        var skill = new DeleteMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroName"] = "Ghost"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<DeleteCommand>(), Arg.Any<CancellationToken>());
    }
}
