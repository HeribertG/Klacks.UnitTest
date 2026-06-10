// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using System.Text.Json;
using Klacks.Api.Application.DTOs.Email;
using Klacks.Api.Application.Queries.Email;
using Klacks.Api.Application.Skills;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ReadEmailSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewSettings" }
    };

    private static ReceivedEmailResource Email(Guid id) => new()
    {
        Id = id,
        Subject = "Shift swap request",
        FromAddress = "anna@example.com",
        FromName = "Anna Muster",
        ToAddress = "office@klacks.ch",
        Folder = "INBOX",
        ReceivedDate = new DateTime(2026, 6, 9, 8, 30, 0, DateTimeKind.Utc),
        IsRead = false,
        HasAttachments = false
    };

    [Test]
    public async Task Read_WithBodyText_ReturnsPlainText()
    {
        var emailId = Guid.NewGuid();
        var email = Email(emailId);
        email.BodyText = "Hello, can I swap my shift on Friday?";
        email.BodyHtml = "<p>ignored</p>";
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns(email);
        var skill = new ReadEmailSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["emailId"] = emailId.ToString()
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("Shift swap request");
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("Id").GetGuid().ShouldBe(emailId);
        data.GetProperty("From").GetString().ShouldBe("Anna Muster <anna@example.com>");
        data.GetProperty("Body").GetString().ShouldBe("Hello, can I swap my shift on Friday?");
        data.GetProperty("BodyTruncated").GetBoolean().ShouldBeFalse();
        await mediator.Received(1).Send(
            Arg.Is<GetReceivedEmailQuery>(q => q.Id == emailId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Read_HtmlOnly_StripsHtmlToText()
    {
        var emailId = Guid.NewGuid();
        var email = Email(emailId);
        email.BodyText = null;
        email.BodyHtml = "<html><head><style>.x{color:red}</style></head><body><p>Hello <b>World</b></p><script>alert('x')</script><div>Second &amp; line</div></body></html>";
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns(email);
        var skill = new ReadEmailSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["emailId"] = emailId.ToString()
        });

        result.Success.ShouldBeTrue();
        var body = JsonSerializer.SerializeToElement(result.Data).GetProperty("Body").GetString()!;
        body.ShouldContain("Hello World");
        body.ShouldContain("Second & line");
        body.ShouldNotContain("<");
        body.ShouldNotContain("alert");
        body.ShouldNotContain("color:red");
    }

    [Test]
    public async Task Read_LongBody_IsTruncated()
    {
        var emailId = Guid.NewGuid();
        var email = Email(emailId);
        email.BodyText = new string('a', 6000);
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns(email);
        var skill = new ReadEmailSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["emailId"] = emailId.ToString()
        });

        result.Success.ShouldBeTrue();
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("BodyTruncated").GetBoolean().ShouldBeTrue();
        data.GetProperty("Body").GetString()!.Length.ShouldBeLessThan(6000);
        data.GetProperty("Body").GetString()!.ShouldEndWith("[body truncated]");
    }

    [Test]
    public async Task Read_UnknownEmail_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns((ReceivedEmailResource?)null);
        var skill = new ReadEmailSkill(mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["emailId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("not found");
    }

    [Test]
    public async Task Read_MissingEmailId_Throws()
    {
        var mediator = Substitute.For<IMediator>();
        var skill = new ReadEmailSkill(mediator);

        await Should.ThrowAsync<ArgumentException>(
            () => skill.ExecuteAsync(Ctx(), new Dictionary<string, object>()));
    }
}
