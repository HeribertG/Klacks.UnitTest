// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the macro CRUD skills: create_macro dispatches a PostCommand with the script as
/// content, list_macros projects and filters the ListQuery result with each macro's origin and returns
/// the script text only on request, and delete_macro resolves a
/// macro by id or unambiguous name before dispatching a DeleteCommand; it deletes macros the assistant created or
/// extended as a copy and reports an unknown id as not found.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands.Settings.Macros;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Queries.Settings.Macros;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Settings;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Services.Macros;

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
        var skill = new CreateMacroSkill(mediator, new MacroOutputChannelInspector());

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
                c.model.Description != null &&
                c.Origin == MacroOrigin.Assistant),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateMacro_UnsupportedOutputChannel_ReturnsError_NoDispatch()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new CreateMacroSkill(mediator, new MacroOutputChannelInspector());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "MyMacro",
            ["script"] = "OUTPUT 1, 0\nOUTPUT 20, 1"
        });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("20");
        await mediator.DidNotReceive().Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateMacro_ComputedOutputChannel_ReturnsError_NoDispatch()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new CreateMacroSkill(mediator, new MacroOutputChannelInspector());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "MyMacro",
            ["script"] = "DIM c\nc = 10\nOUTPUT c, 1"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PostCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task CreateMacro_MissingScript_ReturnsError_NoDispatch()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new CreateMacroSkill(mediator, new MacroOutputChannelInspector());

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
        var skill = new CreateMacroSkill(mediator, new MacroOutputChannelInspector());

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
    public async Task ListMacros_Default_ReturnsOriginWithoutScript()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource>
            {
                new() { Id = Guid.NewGuid(), Name = "AllShift", Content = "OUTPUT 1, 0", Origin = MacroOrigin.Seed }
            }.AsEnumerable());
        var skill = new ListMacrosSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var macro = JsonSerializer.SerializeToElement(result.Data).GetProperty("Macros")[0];
        macro.GetProperty("Origin").GetString().ShouldBe("Seed");
        macro.TryGetProperty("Script", out _).ShouldBeFalse();
    }

    [Test]
    public async Task ListMacros_IncludeScript_ReturnsTheScriptText()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource>
            {
                new() { Id = Guid.NewGuid(), Name = "AllShift", Content = "OUTPUT 1, 0", Origin = MacroOrigin.Seed }
            }.AsEnumerable());
        var skill = new ListMacrosSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["includeScript"] = true });

        var macro = JsonSerializer.SerializeToElement(result.Data).GetProperty("Macros")[0];
        macro.GetProperty("Script").GetString().ShouldBe("OUTPUT 1, 0");
        macro.GetProperty("Origin").GetString().ShouldBe("Seed");
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
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>())
            .Returns(new MacroResource { Id = targetId, Name = "Sunday rate", Origin = MacroOrigin.Assistant });
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

    [TestCase(MacroOrigin.Seed)]
    [TestCase(MacroOrigin.Import)]
    [TestCase(MacroOrigin.User)]
    public async Task DeleteMacro_MacroNotCreatedByAssistant_IsRejected_NoDelete(MacroOrigin origin)
    {
        var targetId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>())
            .Returns(new MacroResource { Id = targetId, Name = "Vacation", Origin = origin });
        var skill = new DeleteMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = targetId.ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("only delete macros it created itself");
        await mediator.DidNotReceive().Send(Arg.Any<DeleteCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteMacro_SeedMacroResolvedByMacroName_IsRejected_NoDelete()
    {
        var seedId = SeededMacroIds.Vacation;
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource>
            {
                new() { Id = seedId, Name = "Vacation", Origin = MacroOrigin.Seed },
                new() { Id = Guid.NewGuid(), Name = "Night rate", Origin = MacroOrigin.Assistant }
            }.AsEnumerable());
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>())
            .Returns(new MacroResource { Id = seedId, Name = "Vacation", Origin = MacroOrigin.Seed });
        var skill = new DeleteMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroName"] = "Vacation"
        });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("is a template shipped with Klacks");
        result.Message.ShouldContain("only delete macros it created itself");
        await mediator.Received(1).Send(Arg.Is<GetQuery>(q => q.Id == seedId), Arg.Any<CancellationToken>());
        await mediator.DidNotReceive().Send(Arg.Any<DeleteCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteMacro_AssistantMacroStillReferenced_RelaysReferenceMessage()
    {
        var targetId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>())
            .Returns(new MacroResource { Id = targetId, Name = "Mine", Origin = MacroOrigin.Assistant });
        mediator.Send(Arg.Any<DeleteCommand>(), Arg.Any<CancellationToken>())
            .Returns<MacroResource>(_ => throw new InvalidRequestException("Macro 'Mine' is still referenced by 1 absence type(s)."));
        var skill = new DeleteMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = targetId.ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Macro 'Mine' is still referenced by 1 absence type(s).");
    }

    [Test]
    public async Task DeleteMacro_UnknownId_ReturnsNotFound_NoDelete()
    {
        var unknownId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>()).Returns((MacroResource?)null);
        var skill = new DeleteMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = unknownId.ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe($"No macro found with id '{unknownId}'.");
        await mediator.DidNotReceive().Send(Arg.Any<DeleteCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteMacro_ExtendedCopyOfTheAssistant_IsDeleted()
    {
        var targetId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>())
            .Returns(new MacroResource { Id = targetId, Name = "AllShift plus", Origin = MacroOrigin.AssistantExtension });
        mediator.Send(Arg.Any<DeleteCommand>(), Arg.Any<CancellationToken>())
            .Returns(new MacroResource { Id = targetId, Name = "AllShift plus" });
        var skill = new DeleteMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = targetId.ToString()
        });

        result.Success.ShouldBeTrue(result.Message);
        await mediator.Received(1).Send(Arg.Is<DeleteCommand>(c => c.Id == targetId), Arg.Any<CancellationToken>());
    }
}
