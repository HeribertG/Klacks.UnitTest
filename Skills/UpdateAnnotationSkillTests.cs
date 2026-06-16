// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for update_annotation: loads via GetQuery&lt;AnnotationResource&gt;, replaces the note
/// text and dispatches PutCommand&lt;AnnotationResource&gt;; an unknown id yields an error.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateAnnotationSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanEditClients" }
    };

    private static AnnotationResource Annotation(Guid id) => new()
    {
        Id = id,
        ClientId = Guid.NewGuid(),
        Note = "old note"
    };

    [Test]
    public async Task UpdateNote_DispatchesPutCommand_WithNewText()
    {
        var id = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<AnnotationResource>>(), Arg.Any<CancellationToken>())
            .Returns(Annotation(id));
        mediator.Send(Arg.Any<PutCommand<AnnotationResource>>(), Arg.Any<CancellationToken>())
            .Returns(ci => ((PutCommand<AnnotationResource>)ci[0]).Resource);
        var skill = new UpdateAnnotationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["annotationId"] = id.ToString(),
            ["note"] = "new note"
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<PutCommand<AnnotationResource>>(c =>
                c.Resource.Id == id &&
                c.Resource.Note == "new note"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownId_ReturnsError_NoPut()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<AnnotationResource>>(), Arg.Any<CancellationToken>())
            .Returns<AnnotationResource>(_ => throw new KeyNotFoundException());
        var skill = new UpdateAnnotationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["annotationId"] = Guid.NewGuid().ToString(),
            ["note"] = "new note"
        });

        result.Success.ShouldBeFalse();
        await mediator.DidNotReceive().Send(Arg.Any<PutCommand<AnnotationResource>>(), Arg.Any<CancellationToken>());
    }
}
