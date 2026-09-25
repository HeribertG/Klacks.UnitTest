// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for how a macro gets its origin: the mapper never takes Origin from a client payload, the
/// resource carries the persisted origin outward, and the PostCommand decides the origin of a new row
/// (User by default for the admin REST path, Assistant when a Klacksy skill creates it).
/// </summary>

using Klacks.Api.Application.Commands.Settings.Macros;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Handlers.Settings.Macro;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Macros;
using Microsoft.Extensions.Logging;
using MacroEntity = Klacks.Api.Domain.Models.Settings.Macro;

namespace Klacks.UnitTest.Application.Handlers.Settings.Macros;

[TestFixture]
public class MacroOriginAssignmentTests
{
    private ISettingsRepository _settingsRepository = null!;
    private IMacroScriptValidator _macroScriptValidator = null!;
    private IUnitOfWork _unitOfWork = null!;
    private SettingsMapper _mapper = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _macroScriptValidator = Substitute.For<IMacroScriptValidator>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _mapper = new SettingsMapper();

        _macroScriptValidator.Validate(Arg.Any<string>()).Returns(MacroScriptValidationResult.Success());
        _settingsRepository.AddMacroAsync(Arg.Any<MacroEntity>()).Returns(ci => ci.Arg<MacroEntity>());
    }

    private PostCommandHandler CreateHandler() => new(
        _settingsRepository,
        _mapper,
        _macroScriptValidator,
        _unitOfWork,
        Substitute.For<ILogger<PostCommandHandler>>());

    private static MacroResource PayloadClaimingAssistant() => new()
    {
        Id = Guid.NewGuid(),
        Name = "Sunday rate",
        Content = "OUTPUT 1, 0",
        Description = new MultiLanguage(),
        Origin = MacroOrigin.Assistant
    };

    [Test]
    public void ToMacroEntity_IgnoresClientSuppliedOrigin()
    {
        var entity = _mapper.ToMacroEntity(PayloadClaimingAssistant());

        entity.Origin.ShouldBe(MacroOrigin.User);
    }

    [Test]
    public void ToMacroResource_CarriesPersistedOrigin()
    {
        var entity = new MacroEntity
        {
            Id = Guid.NewGuid(),
            Name = "AllShift",
            Description = new MultiLanguage(),
            Origin = MacroOrigin.Seed
        };

        var resource = _mapper.ToMacroResource(entity);

        resource.Origin.ShouldBe(MacroOrigin.Seed);
    }

    [Test]
    public async Task Post_DefaultCommand_PersistsUserOrigin_EvenWhenPayloadClaimsAssistant()
    {
        await CreateHandler().Handle(new PostCommand(PayloadClaimingAssistant()), CancellationToken.None);

        await _settingsRepository.Received(1).AddMacroAsync(Arg.Is<MacroEntity>(m => m.Origin == MacroOrigin.User));
    }

    [Test]
    public async Task Post_AssistantCommand_PersistsAssistantOrigin()
    {
        var payload = PayloadClaimingAssistant();
        payload.Origin = MacroOrigin.User;

        await CreateHandler().Handle(new PostCommand(payload, MacroOrigin.Assistant), CancellationToken.None);

        await _settingsRepository.Received(1).AddMacroAsync(Arg.Is<MacroEntity>(m => m.Origin == MacroOrigin.Assistant));
    }
}
