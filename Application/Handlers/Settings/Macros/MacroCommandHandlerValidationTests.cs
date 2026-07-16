// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the macro Post/PutCommandHandler script validation gate: an invalid script is
/// rejected with an InvalidRequestException carrying the validator message and nothing is
/// persisted; a valid or empty script passes through to the settings repository.
/// </summary>

using Klacks.Api.Application.Commands.Settings.Macros;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.Handlers.Settings.Macro;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Models.Macros;
using Microsoft.Extensions.Logging;
using MacroEntity = Klacks.Api.Domain.Models.Settings.Macro;

namespace Klacks.UnitTest.Application.Handlers.Settings.Macros;

[TestFixture]
public class MacroCommandHandlerValidationTests
{
    private const string ValidatorErrorMessage = "compile error: something is wrong";

    private ISettingsRepository _settingsRepository = null!;
    private SettingsMapper _settingsMapper = null!;
    private IMacroScriptValidator _macroScriptValidator = null!;
    private IUnitOfWork _unitOfWork = null!;

    [SetUp]
    public void SetUp()
    {
        _settingsRepository = Substitute.For<ISettingsRepository>();
        _settingsMapper = new SettingsMapper();
        _macroScriptValidator = Substitute.For<IMacroScriptValidator>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _settingsRepository.AddMacroAsync(Arg.Any<MacroEntity>()).Returns(ci => ci.Arg<MacroEntity>());
        _settingsRepository.PutMacroAsync(Arg.Any<MacroEntity>()).Returns(ci => ci.Arg<MacroEntity>());
    }

    private PostCommandHandler CreatePostHandler() => new(
        _settingsRepository,
        _settingsMapper,
        _macroScriptValidator,
        _unitOfWork,
        Substitute.For<ILogger<PostCommandHandler>>());

    private PutCommandHandler CreatePutHandler() => new(
        _settingsRepository,
        _settingsMapper,
        _macroScriptValidator,
        _unitOfWork,
        Substitute.For<ILogger<PutCommandHandler>>());

    private static MacroResource Resource(string? content) => new()
    {
        Id = Guid.NewGuid(),
        Name = "Sunday rate",
        Content = content!
    };

    [Test]
    public async Task Post_InvalidScript_ThrowsInvalidRequest_NoPersist()
    {
        _macroScriptValidator.Validate(Arg.Any<string>())
            .Returns(MacroScriptValidationResult.Failure(ValidatorErrorMessage));

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            () => CreatePostHandler().Handle(new PostCommand(Resource("DIM 123abc")), CancellationToken.None));

        ex.Message.ShouldBe(ValidatorErrorMessage);
        await _settingsRepository.DidNotReceive().AddMacroAsync(Arg.Any<MacroEntity>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Post_ValidScript_ValidatesAndPersists()
    {
        _macroScriptValidator.Validate(Arg.Any<string>())
            .Returns(MacroScriptValidationResult.Success());

        var result = await CreatePostHandler().Handle(
            new PostCommand(Resource("OUTPUT 1, 0")), CancellationToken.None);

        result.ShouldNotBeNull();
        _macroScriptValidator.Received(1).Validate("OUTPUT 1, 0");
        await _settingsRepository.Received(1).AddMacroAsync(Arg.Any<MacroEntity>());
    }

    [Test]
    public async Task Post_EmptyContent_SkipsValidation_Persists()
    {
        var result = await CreatePostHandler().Handle(
            new PostCommand(Resource(string.Empty)), CancellationToken.None);

        result.ShouldNotBeNull();
        _macroScriptValidator.DidNotReceive().Validate(Arg.Any<string>());
        await _settingsRepository.Received(1).AddMacroAsync(Arg.Any<MacroEntity>());
    }

    [Test]
    public async Task Put_InvalidScript_ThrowsInvalidRequest_NoPersist()
    {
        _macroScriptValidator.Validate(Arg.Any<string>())
            .Returns(MacroScriptValidationResult.Failure(ValidatorErrorMessage));

        var ex = await Should.ThrowAsync<InvalidRequestException>(
            () => CreatePutHandler().Handle(new PutCommand(Resource("DIM 123abc")), CancellationToken.None));

        ex.Message.ShouldBe(ValidatorErrorMessage);
        await _settingsRepository.DidNotReceive().PutMacroAsync(Arg.Any<MacroEntity>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task Put_ValidScript_ValidatesAndPersists()
    {
        _macroScriptValidator.Validate(Arg.Any<string>())
            .Returns(MacroScriptValidationResult.Success());

        var result = await CreatePutHandler().Handle(
            new PutCommand(Resource("OUTPUT 1, 0")), CancellationToken.None);

        result.ShouldNotBeNull();
        _macroScriptValidator.Received(1).Validate("OUTPUT 1, 0");
        await _settingsRepository.Received(1).PutMacroAsync(Arg.Any<MacroEntity>());
    }
}
