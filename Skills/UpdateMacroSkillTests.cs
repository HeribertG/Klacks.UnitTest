// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for update_macro: the skill resolves the macro by id or unambiguous name, loads it
/// via GetQuery, merges only the provided fields (name, script, description) onto the current
/// resource, dispatches a PutCommand marked as an assistant edit with the merged result, relays script validation
/// errors and appends the customer-owned hint when the script of a standard-function macro is changed. Extended
/// copies may be renamed and re-described but their script never changes; new scripts are refused on unsupported
/// OUTPUT channels, and new names are refused when another macro or a template shipped with Klacks carries them.
/// </summary>

using Klacks.Api.Application.Commands.Settings.Macros;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Queries.Settings.Macros;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Infrastructure.Mediator;
using Klacks.Api.Infrastructure.Services.Macros;

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

    private static MacroResource Existing(Guid id, int type = (int)MacroFunctionEnum.Standard, MacroOrigin origin = MacroOrigin.Assistant) => new()
    {
        Id = id,
        Name = "Sunday rate",
        Content = "OUTPUT 1, 0",
        Type = type,
        Description = new MultiLanguage { De = "Alt" },
        Origin = origin
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
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = id.ToString(),
            ["name"] = "Holiday rate",
            ["script"] = "OUTPUT 1, 2"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand>(c =>
                c.ByAssistant &&
                c.model.Id == id &&
                c.model.Name == "Holiday rate" &&
                c.model.Content == "OUTPUT 1, 2"),
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
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = id.ToString(),
            ["script"] = "OUTPUT 1, 3"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand>(c =>
                c.model.Name == "Sunday rate" &&
                c.model.Content == "OUTPUT 1, 3"),
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
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

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
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

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
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

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
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

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
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

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
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

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
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

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
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

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
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = id.ToString(),
            ["script"] = "OUTPUT 1, 2"
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
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = id.ToString(),
            ["script"] = "OUTPUT 1, 2"
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
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

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
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = id.ToString(),
            ["script"] = "DIM 123abc"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("compile error: bad script");
    }

    [TestCase(MacroOrigin.Seed)]
    [TestCase(MacroOrigin.Import)]
    [TestCase(MacroOrigin.User)]
    public async Task UpdateMacro_MacroNotCreatedByAssistant_IsRejected_NoPut(MacroOrigin origin)
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>()).Returns(Existing(id, origin: origin));
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = id.ToString(),
            ["script"] = "OUTPUT 1, 1"
        });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("only change macros it created itself");
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateMacro_ExtendedCopy_ScriptChange_IsRejected_NoPut()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>())
            .Returns(Existing(id, origin: MacroOrigin.AssistantExtension));
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = id.ToString(),
            ["script"] = "OUTPUT 1, 1"
        });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("extended copy");
        result.Message.ShouldContain("extend_macro");
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateMacro_ExtendedCopy_RenameAndDescription_WithUnchangedScript_IsStoredAsAssistantEdit()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery>(), Arg.Any<CancellationToken>())
            .Returns(Existing(id, origin: MacroOrigin.AssistantExtension));
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource> { Existing(id, origin: MacroOrigin.AssistantExtension) }.AsEnumerable());
        mediator.Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand)ci[0]).model);
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = id.ToString(),
            ["name"] = "Sunday rate plus",
            ["script"] = "OUTPUT 1, 0",
            ["description"] = "Copy"
        });

        result.Success.ShouldBeTrue(result.Message);
        await mediator.Received(1).Send(
            Arg.Is<PutCommand>(c => c.ByAssistant && c.model.Name == "Sunday rate plus" && c.model.Content == "OUTPUT 1, 0"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateMacro_ScriptWithUnsupportedChannel_IsRejected_NoPut()
    {
        var id = Guid.NewGuid();
        var mediator = MediatorFor(id, (int)MacroFunctionEnum.Custom);
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = id.ToString(),
            ["script"] = "OUTPUT 1, 0\nOUTPUT 20, 1"
        });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("20");
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateMacro_NameOfAnotherMacro_IsRejected_NoPut()
    {
        var id = Guid.NewGuid();
        var mediator = MediatorFor(id, (int)MacroFunctionEnum.Custom);
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource>
            {
                Existing(id),
                new() { Id = Guid.NewGuid(), Name = "Night rate", Origin = MacroOrigin.User }
            }.AsEnumerable());
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = id.ToString(),
            ["name"] = " night RATE "
        });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("already exists");
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateMacro_OwnNameInOtherCase_IsAllowed()
    {
        var id = Guid.NewGuid();
        var mediator = MediatorFor(id, (int)MacroFunctionEnum.Custom);
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource> { Existing(id) }.AsEnumerable());
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = id.ToString(),
            ["name"] = "SUNDAY RATE"
        });

        result.Success.ShouldBeTrue(result.Message);
        await mediator.Received(1).Send(Arg.Is<PutCommand>(c => c.model.Name == "SUNDAY RATE"), Arg.Any<CancellationToken>());
    }

    [TestCase(SeededMacroNames.AllShift)]
    [TestCase("vacation50%")]
    public async Task UpdateMacro_NameOfATemplate_IsRejected_EvenWhenTheTemplateIsGone(string templateName)
    {
        var id = Guid.NewGuid();
        var mediator = MediatorFor(id, (int)MacroFunctionEnum.Custom);
        mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<MacroResource> { Existing(id) }.AsEnumerable());
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroId"] = id.ToString(),
            ["name"] = templateName
        });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("template macro shipped with Klacks");
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UpdateMacro_SeedMacroResolvedByMacroName_IsRejected_NoPut()
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
            .Returns(new MacroResource { Id = seedId, Name = "Vacation", Content = "OUTPUT 1, 0", Origin = MacroOrigin.Seed });
        var skill = new UpdateMacroSkill(mediator, new MacroOutputChannelInspector());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["macroName"] = "Vacation",
            ["script"] = "OUTPUT 1, 1"
        });

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("is a template shipped with Klacks");
        result.Message.ShouldContain("only change macros it created itself");
        await mediator.Received(1).Send(Arg.Is<GetQuery>(q => q.Id == seedId), Arg.Any<CancellationToken>());
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand>(), Arg.Any<CancellationToken>());
    }
}
