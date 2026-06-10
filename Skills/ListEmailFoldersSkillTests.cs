// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text.Json;
using Klacks.Api.Application.DTOs.Email;
using Klacks.Api.Application.Queries.Email;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListEmailFoldersSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewSettings" }
    };

    [Test]
    public async Task List_ReturnsFoldersWithCounts()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetEmailFoldersQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<EmailFolderResource>
            {
                new() { Id = Guid.NewGuid(), Name = "Inbox", ImapFolderName = "INBOX", IsSystem = true, UnreadCount = 3, TotalCount = 12 },
                new() { Id = Guid.NewGuid(), Name = "Invoices", ImapFolderName = "Invoices", IsSystem = false, UnreadCount = 1, TotalCount = 5 }
            });
        var skill = new ListEmailFoldersSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("2 email folders");
        result.Message.ShouldContain("4 unread");
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("Count").GetInt32().ShouldBe(2);
        data.GetProperty("TotalUnread").GetInt32().ShouldBe(4);
        data.GetProperty("Folders")[0].GetProperty("ImapFolderName").GetString().ShouldBe("INBOX");
        await mediator.Received(1).Send(Arg.Any<GetEmailFoldersQuery>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_Empty_ReturnsZeroCount()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetEmailFoldersQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<EmailFolderResource>());
        var skill = new ListEmailFoldersSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("0 email folders");
    }
}
