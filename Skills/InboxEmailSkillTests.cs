// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the inbox housekeeping skills — mark_email_read, fetch_new_emails,
/// move_email_to_folder (special-use alias resolution), delete_email, restore_email,
/// get_email_analysis and translate_email. Covers happy paths with the verified marker,
/// no-op short circuits and the verification-failure paths.
/// </summary>

using Klacks.Api.Application.Commands.Email;
using Klacks.Api.Application.DTOs.Email;
using Klacks.Api.Application.Queries.Email;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Email;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class InboxEmailSkillTests
{
    private IMediator _mediator = null!;
    private IEmailFolderRepository _folderRepository = null!;
    private IEmailAnalysisRepository _analysisRepository = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _folderRepository = Substitute.For<IEmailFolderRepository>();
        _analysisRepository = Substitute.For<IEmailAnalysisRepository>();
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Trash).Returns("Deleted Items");
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Junk).Returns("Spam");
        _folderRepository.GetImapNameBySpecialUseAsync(FolderSpecialUse.Inbox).Returns("INBOX");
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewSettings" }
    };

    private static ReceivedEmailResource Email(Guid id, string folder = "INBOX", bool isRead = false) => new()
    {
        Id = id,
        Subject = "Shift swap request",
        FromAddress = "anna@example.com",
        Folder = folder,
        IsRead = isRead
    };

    private static Dictionary<string, object> P(Guid emailId) => new() { ["emailId"] = emailId.ToString() };

    [Test]
    public async Task MarkEmailRead_MarksAndVerifies()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Email(id, isRead: false), Email(id, isRead: true));
        _mediator.Send(Arg.Any<MarkEmailAsReadCommand>(), Arg.Any<CancellationToken>()).Returns(true);
        var skill = new MarkEmailReadSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), P(id));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("verified");
        await _mediator.Received(1).Send(
            Arg.Is<MarkEmailAsReadCommand>(c => c.Id == id && c.IsRead), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MarkEmailRead_NoOp_WhenAlreadyInRequestedState()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Email(id, isRead: true));
        var skill = new MarkEmailReadSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), P(id));

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("nothing to change");
        await _mediator.DidNotReceive().Send(Arg.Any<MarkEmailAsReadCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MarkEmailRead_ReturnsError_WhenVerificationShowsOldState()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Email(id, isRead: false), Email(id, isRead: false));
        _mediator.Send(Arg.Any<MarkEmailAsReadCommand>(), Arg.Any<CancellationToken>()).Returns(true);
        var skill = new MarkEmailReadSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), P(id));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("verification failed");
    }

    [Test]
    public async Task FetchNewEmails_ReportsFetchedCount()
    {
        _mediator.Send(Arg.Any<FetchEmailsCommand>(), Arg.Any<CancellationToken>())
            .Returns(new FetchEmailsResult(true, 3));
        var skill = new FetchNewEmailsSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("3 new email(s)");
    }

    [Test]
    public async Task FetchNewEmails_ReturnsError_OnImapFailure()
    {
        _mediator.Send(Arg.Any<FetchEmailsCommand>(), Arg.Any<CancellationToken>())
            .Returns(new FetchEmailsResult(false, 0, "connection refused"));
        var skill = new FetchNewEmailsSkill(_mediator);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("connection refused");
    }

    [Test]
    public async Task MoveEmail_ResolvesJunkAlias_AndVerifies()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Email(id, folder: "INBOX"), Email(id, folder: "Spam"));
        _mediator.Send(Arg.Any<MoveEmailToFolderCommand>(), Arg.Any<CancellationToken>()).Returns(true);
        var skill = new MoveEmailToFolderSkill(_mediator, _folderRepository);

        var parameters = P(id);
        parameters["folder"] = "spam";
        var result = await skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("verified");
        await _mediator.Received(1).Send(
            Arg.Is<MoveEmailToFolderCommand>(c => c.Id == id && c.Folder == "Spam"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MoveEmail_ReturnsError_WhenLiteralFolderUnknown()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Email(id));
        _folderRepository.ExistsByImapNameAsync("Projekte").Returns(false);
        var skill = new MoveEmailToFolderSkill(_mediator, _folderRepository);

        var parameters = P(id);
        parameters["folder"] = "Projekte";
        var result = await skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("does not exist");
        await _mediator.DidNotReceive().Send(Arg.Any<MoveEmailToFolderCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MoveEmail_NoOp_WhenAlreadyInTargetFolder()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Email(id, folder: "Spam"));
        var skill = new MoveEmailToFolderSkill(_mediator, _folderRepository);

        var parameters = P(id);
        parameters["folder"] = "junk";
        var result = await skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("nothing to move");
        await _mediator.DidNotReceive().Send(Arg.Any<MoveEmailToFolderCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteEmail_MovesToTrash_AndVerifies()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Email(id, folder: "INBOX"), Email(id, folder: "Deleted Items"));
        _mediator.Send(Arg.Any<DeleteReceivedEmailCommand>(), Arg.Any<CancellationToken>()).Returns(true);
        var skill = new DeleteEmailSkill(_mediator, _folderRepository);

        var result = await skill.ExecuteAsync(Ctx(), P(id));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("verified");
        result.Message.ShouldContain("restore_email");
    }

    [Test]
    public async Task DeleteEmail_ReturnsError_WhenStillOutsideTrash()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Email(id, folder: "INBOX"), Email(id, folder: "INBOX"));
        _mediator.Send(Arg.Any<DeleteReceivedEmailCommand>(), Arg.Any<CancellationToken>()).Returns(true);
        var skill = new DeleteEmailSkill(_mediator, _folderRepository);

        var result = await skill.ExecuteAsync(Ctx(), P(id));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("verification failed");
    }

    [Test]
    public async Task RestoreEmail_ReturnsError_WhenEmailNotInTrash()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Email(id, folder: "INBOX"));
        var skill = new RestoreEmailSkill(_mediator, _folderRepository);

        var result = await skill.ExecuteAsync(Ctx(), P(id));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not in the trash");
        await _mediator.DidNotReceive().Send(Arg.Any<RestoreEmailCommand>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task RestoreEmail_RestoresAndVerifies()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Email(id, folder: "Deleted Items"), Email(id, folder: "INBOX"));
        _mediator.Send(Arg.Any<RestoreEmailCommand>(), Arg.Any<CancellationToken>()).Returns(true);
        var skill = new RestoreEmailSkill(_mediator, _folderRepository);

        var result = await skill.ExecuteAsync(Ctx(), P(id));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("verified");
    }

    [Test]
    public async Task GetEmailAnalysis_ReportsIntentAndSummary()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Email(id));
        _analysisRepository.GetByReceivedEmailIdAsync(id, Arg.Any<CancellationToken>())
            .Returns(new EmailAnalysis
            {
                ReceivedEmailId = id,
                Intent = EmailIntent.VacationRequest,
                Summary = "Anna is sick this week.",
                AnalyzedAt = new DateTime(2026, 7, 10, 6, 0, 0, DateTimeKind.Utc)
            });
        var skill = new GetEmailAnalysisSkill(_mediator, _analysisRepository);

        var result = await skill.ExecuteAsync(Ctx(), P(id));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("VacationRequest");
        result.Message.ShouldContain("Anna is sick this week.");
    }

    [Test]
    public async Task GetEmailAnalysis_SaysSo_WhenNotAnalyzed()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns(Email(id));
        _analysisRepository.GetByReceivedEmailIdAsync(id, Arg.Any<CancellationToken>())
            .Returns((EmailAnalysis?)null);
        var skill = new GetEmailAnalysisSkill(_mediator, _analysisRepository);

        var result = await skill.ExecuteAsync(Ctx(), P(id));

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("not been analyzed");
    }

    [Test]
    public async Task TranslateEmail_ReturnsTranslatedSubjectAndBody()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<TranslateReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns(new TranslatedEmailResource
            {
                Subject = "Anfrage Diensttausch",
                BodyText = "Hallo, kann ich meinen Dienst am Freitag tauschen?",
                TargetLanguage = "de"
            });
        var skill = new TranslateEmailSkill(_mediator);

        var parameters = P(id);
        parameters["targetLanguage"] = "DE";
        var result = await skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("Anfrage Diensttausch");
        await _mediator.Received(1).Send(
            Arg.Is<TranslateReceivedEmailQuery>(q => q.Id == id && q.TargetLanguage == "de"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task TranslateEmail_ReturnsError_WhenServiceUnavailable()
    {
        var id = Guid.NewGuid();
        _mediator.Send(Arg.Any<TranslateReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns((TranslatedEmailResource?)null);
        var skill = new TranslateEmailSkill(_mediator);

        var parameters = P(id);
        parameters["targetLanguage"] = "de";
        var result = await skill.ExecuteAsync(Ctx(), parameters);

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("could not be translated");
    }
}
