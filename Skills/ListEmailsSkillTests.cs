// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text.Json;
using Klacks.Api.Application.DTOs.Email;
using Klacks.Api.Application.Queries.Email;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListEmailsSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewSettings" }
    };

    private static ReceivedEmailListResponse Response(params ReceivedEmailListResource[] items) => new()
    {
        Items = items.ToList(),
        TotalCount = items.Length,
        UnreadCount = items.Count(i => !i.IsRead)
    };

    [Test]
    public async Task List_Default_ReturnsCompactProjection()
    {
        var emailId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetReceivedEmailsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Response(
                new ReceivedEmailListResource
                {
                    Id = emailId,
                    FromAddress = "anna@example.com",
                    FromName = "Anna Muster",
                    Subject = "Schedule question",
                    ReceivedDate = new DateTime(2026, 6, 9, 8, 30, 0, DateTimeKind.Utc),
                    IsRead = false,
                    HasAttachments = true
                },
                new ReceivedEmailListResource
                {
                    Id = Guid.NewGuid(),
                    FromAddress = "noreply@example.com",
                    Subject = "Newsletter",
                    IsRead = true
                }));
        var skill = new ListEmailsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("2 of 2 emails");
        result.Message.ShouldContain("1 unread");
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("Count").GetInt32().ShouldBe(2);
        data.GetProperty("Emails")[0].GetProperty("Id").GetGuid().ShouldBe(emailId);
        data.GetProperty("Emails")[0].GetProperty("From").GetString().ShouldBe("Anna Muster <anna@example.com>");
        data.GetProperty("Emails")[1].GetProperty("From").GetString().ShouldBe("noreply@example.com");
        data.GetProperty("Emails")[0].TryGetProperty("Body", out _).ShouldBeFalse();
        await mediator.Received(1).Send(
            Arg.Is<GetReceivedEmailsQuery>(q => q.Skip == 0 && q.Take == 20 && q.Folder == null && q.ReadFilter == null),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_FolderAndUnreadOnly_PassesFiltersToQuery()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetReceivedEmailsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Response());
        var skill = new ListEmailsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["folder"] = "INBOX",
            ["maxResults"] = 5,
            ["unreadOnly"] = true
        });

        result.Success.ShouldBeTrue();
        await mediator.Received(1).Send(
            Arg.Is<GetReceivedEmailsQuery>(q => q.Folder == "INBOX" && q.Take == 5 && q.ReadFilter == "unread"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_MaxResultsOutOfRange_IsClamped()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetReceivedEmailsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Response());
        var skill = new ListEmailsSkill(mediator);

        await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["maxResults"] = 500 });

        await mediator.Received(1).Send(
            Arg.Is<GetReceivedEmailsQuery>(q => q.Take == 50),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task List_Empty_ReturnsZeroCount()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetReceivedEmailsQuery>(), Arg.Any<CancellationToken>())
            .Returns(Response());
        var skill = new ListEmailsSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("0 of 0 emails");
    }
}
