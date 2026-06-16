// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for delete_annotation: dispatches DeleteCommand&lt;AnnotationResource&gt; for the given id
/// and reports success; a null result (unknown annotation) yields an error.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class DeleteAnnotationSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    [Test]
    public async Task DeleteAnnotation_DispatchesDeleteCommand_AndReportsSuccess()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<AnnotationResource>>(), Arg.Any<CancellationToken>())
            .Returns(new AnnotationResource { Id = id });
        var skill = new DeleteAnnotationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["annotationId"] = id.ToString()
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<DeleteCommand<AnnotationResource>>(c => c.Id == id),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteAnnotation_UnknownId_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<DeleteCommand<AnnotationResource>>(), Arg.Any<CancellationToken>())
            .Returns((AnnotationResource?)null);
        var skill = new DeleteAnnotationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["annotationId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
    }
}
