// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for update_macro: the skill resolves the macro by id or unambiguous name, loads it
/// via GetQuery, merges only the provided fields (name, script, description) onto the current
/// resource, dispatches a PutCommand with the merged result, relays script validation errors and
/// appends the customer-owned hint when the script of a standard-function macro is changed.
/// </summary>

using Klacks.Api.Application.Commands.Settings.Macros;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Queries.Settings.Macros;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Exceptions;
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

    private static MacroResource Existing(Guid id, int type = (int)MacroFunctionEnum.Standard) => new()
    {
        Id = id,
        Name = "Sunday rate",
        Content = "OUTPUT 1, 0",
        Type = type,
        Description = new MultiLanguage { De = "Alt" }
    };

    private static IMediator MediatorFor(Guid id, int type = (int)MacroFunctionEnum.Standard)
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>()).Returns(Existing(id, type));
        mediator.Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand)ci[0]).model);
        return mediator;
    }

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

    [Test]
    public async Task UpdateMacro_ByMacroName_ResolvesAndDispatchesPutCommand()
    {
        var id = Guid.NewGuid();
        var mediator = MediatorFor(id);
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource>
            {
                Existing(id),
                new() { Id = Guid.NewGuid(), Name = "Night rate" }
            }.AsEnumerable());
        var skill = new UpdateMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroName"] = "Sunday rate",
            ["name"] = "Holiday rate"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand>(c => c.model.Id == id && c.model.Name == "Holiday rate"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateMacro_AmbiguousMacroName_ReturnsError_NoPut()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource>
            {
                new() { Id = Guid.NewGuid(), Name = "Rate weekend" },
                new() { Id = Guid.NewGuid(), Name = "Rate holiday" }
            }.AsEnumerable());
        var skill = new UpdateMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroName"] = "Rate",
            ["name"] = "Holiday rate"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("ambiguous");
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateMacro_UnknownMacroName_ReturnsError_NoPut()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource>().AsEnumerable());
        var skill = new UpdateMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroName"] = "Ghost",
            ["name"] = "Holiday rate"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("not found");
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateMacro_MissingIdAndName_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new UpdateMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["name"] = "Holiday rate"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Either macroId or macroName must be provided.");
    }

    [Test]
    public async Task UpdateMacro_StandardMacro_ScriptChanged_AppendsCustomerOwnedHint()
    {
        var id = Guid.NewGuid();
        var mediator = MediatorFor(id, (int)MacroFunctionEnum.Standard);
        var skill = new UpdateMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = id.ToString(),
            ["script"] = "OUTPUT 2, 0"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("standard function");
        result.Message.ShouldContain("customer-owned");
    }

    [Test]
    public async Task UpdateMacro_CustomMacro_ScriptChanged_NoHint()
    {
        var id = Guid.NewGuid();
        var mediator = MediatorFor(id, (int)MacroFunctionEnum.Custom);
        var skill = new UpdateMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = id.ToString(),
            ["script"] = "OUTPUT 2, 0"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldNotContain("customer-owned");
    }

    [Test]
    public async Task UpdateMacro_StandardMacro_NameOnlyChange_NoHint()
    {
        var id = Guid.NewGuid();
        var mediator = MediatorFor(id, (int)MacroFunctionEnum.Standard);
        var skill = new UpdateMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = id.ToString(),
            ["name"] = "Holiday rate"
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldNotContain("customer-owned");
    }

    [Test]
    public async Task UpdateMacro_PutRejectsScript_ReturnsValidationErrorMessage()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>()).Returns(Existing(id));
        mediator.Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>())
            .Returns<MacroResource?>(_ => throw new InvalidRequestException("compile error: bad script"));
        var skill = new UpdateMacroSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = id.ToString(),
            ["script"] = "DIM 123abc"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("compile error: bad script");
    }
}
