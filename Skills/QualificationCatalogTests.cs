// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the qualification catalogue: the create handler returns an existing same-named
/// qualification instead of duplicating it (else adds), and the thin create / list skills dispatch
/// and project correctly.
/// </summary>

using System.Text.Json;
using Klacks.Api.Application.Commands.Qualifications;
using Klacks.Api.Application.Handlers.Qualifications;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class QualificationCatalogTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts", "CanEditShifts" }
    };

    [Test]
    public async Task CreateHandler_NoExisting_Adds()
    {
        var repo = Substitute.For<IQualificationRepository>();
        repo.GetByNameAsync("First aid", Arg.Any<CancellationToken>()).Returns((Qualification?)null);
        var uow = Substitute.For<IUnitOfWork>();
        var handler = new CreateQualificationCommandHandler(repo, uow);

        var id = await handler.Handle(new CreateQualificationCommand(new MultiLanguage { De = "First aid" }, null), CancellationToken.None);

        await repo.Received(1).Add(Arg.Is<Qualification>(q => q.Name.De == "First aid"));
        await uow.Received(1).CompleteAsync();
        id.ShouldNotBe(Guid.Empty);
    }

    [Test]
    public async Task CreateHandler_ExistingName_ReturnsExisting_NoAdd()
    {
        var existing = new Qualification { Id = Guid.NewGuid(), Name = new MultiLanguage { De = "First aid" } };
        var repo = Substitute.For<IQualificationRepository>();
        repo.GetByNameAsync("First aid", Arg.Any<CancellationToken>()).Returns(existing);
        var uow = Substitute.For<IUnitOfWork>();
        var handler = new CreateQualificationCommandHandler(repo, uow);

        var id = await handler.Handle(new CreateQualificationCommand(new MultiLanguage { De = "First aid" }, null), CancellationToken.None);

        id.ShouldBe(existing.Id);
        await repo.DidNotReceive().Add(Arg.Any<Qualification>());
    }

    [Test]
    public async Task CreateSkill_Dispatches()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<CreateQualificationCommand>(), Arg.Any<CancellationToken>()).Returns(Guid.NewGuid());
        var skill = new CreateQualificationSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["name"] = "Forklift" });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<CreateQualificationCommand>(c => c.Name.De == "Forklift"), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ListSkill_ProjectsRepository()
    {
        var repo = Substitute.For<IQualificationRepository>();
        repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<Qualification>
        {
            new() { Id = Guid.NewGuid(), Name = new MultiLanguage { De = "First aid" }, Description = new MultiLanguage { De = "Basic" } },
            new() { Id = Guid.NewGuid(), Name = new MultiLanguage { De = "Forklift" } }
        });
        var skill = new ListQualificationsSkill(repo);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("Count").GetInt32().ShouldBe(2);
        data.GetProperty("Qualifications")[0].GetProperty("Name").GetProperty("de").GetString().ShouldBe("First aid");
    }
}
