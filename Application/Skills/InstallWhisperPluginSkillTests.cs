// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for InstallWhisperPluginSkill: it accepts all three engine aliases (small, large-v3-turbo,
/// parakeet-v3), queues the install command with the alias, rejects an unknown alias with an error that
/// names every supported alias, and surfaces a rejected enqueue as an error.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.Constants;
using Klacks.Api.Application.DTOs.Update;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Application.Skills;

[TestFixture]
public class InstallWhisperPluginSkillTests
{
    private const string ModelAliasParameter = "modelAlias";

    private IMediator _mediator = null!;
    private InstallWhisperPluginSkill _skill = null!;

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "admin",
        UserPermissions = new List<string>()
    };

    [SetUp]
    public void SetUp()
    {
        _mediator = Substitute.For<IMediator>();
        _skill = new InstallWhisperPluginSkill(_mediator);
    }

    [TestCase(WhisperPluginConstants.ModelAliasSmall)]
    [TestCase(WhisperPluginConstants.ModelAliasLarge)]
    [TestCase(WhisperPluginConstants.ModelAliasParakeet)]
    public async Task ExecuteAsync_QueuesInstallForEverySupportedAlias(string alias)
    {
        _mediator.Send(Arg.Any<InstallWhisperPluginCommand>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateTriggerResult { Enqueued = true, OperationId = Guid.NewGuid() });

        var result = await _skill.ExecuteAsync(
            Context(), new Dictionary<string, object> { [ModelAliasParameter] = alias });

        result.Success.ShouldBeTrue();
        await _mediator.Received(1).Send(
            Arg.Is<InstallWhisperPluginCommand>(c => c.ModelAlias == alias), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_RejectsUnknownAliasAndListsAllThreeAliases()
    {
        var result = await _skill.ExecuteAsync(
            Context(), new Dictionary<string, object> { [ModelAliasParameter] = "gigantic" });

        result.Success.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain(WhisperPluginConstants.ModelAliasSmall);
        result.Message.ShouldContain(WhisperPluginConstants.ModelAliasLarge);
        result.Message.ShouldContain(WhisperPluginConstants.ModelAliasParakeet);
        await _mediator.DidNotReceiveWithAnyArgs().Send<UpdateTriggerResult>(default!, default);
    }

    [Test]
    public async Task ExecuteAsync_ReportsRejectedEnqueueAsError()
    {
        _mediator.Send(Arg.Any<InstallWhisperPluginCommand>(), Arg.Any<CancellationToken>())
            .Returns(new UpdateTriggerResult { Enqueued = false, Reason = WhisperPluginReasons.UnknownModel });

        var result = await _skill.ExecuteAsync(
            Context(), new Dictionary<string, object> { [ModelAliasParameter] = WhisperPluginConstants.ModelAliasParakeet });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain(WhisperPluginReasons.UnknownModel);
    }
}
