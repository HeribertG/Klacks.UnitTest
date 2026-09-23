// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the get_email_analysis skill: unknown email, not yet analyzed, and an analyzed email
/// whose clarification question expired unanswered (shown in plain words, no internal status name).
/// </summary>

using Klacks.Api.Application.DTOs.Email;
using Klacks.Api.Application.Queries.Email;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces.Inbound;
using Klacks.Api.Domain.Models.Inbound;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetEmailAnalysisSkillTests
{
    private IMediator _mediator = null!;
    private IInboundAnalysisRepository _analysisRepository = null!;
    private IInboundClarificationRepository _clarificationRepository = null!;
    private GetEmailAnalysisSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _mediator = Substitute.For<IMediator>();
        _analysisRepository = Substitute.For<IInboundAnalysisRepository>();
        _clarificationRepository = Substitute.For<IInboundClarificationRepository>();
        _skill = new GetEmailAnalysisSkill(_mediator, _analysisRepository, _clarificationRepository);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewSettings" }
    };

    private static Dictionary<string, object> P(Guid emailId) => new() { ["emailId"] = emailId.ToString() };

    [Test]
    public async Task UnknownEmail_IsAnError()
    {
        _mediator.Send(Arg.Any<GetReceivedEmailQuery>(), Arg.Any<CancellationToken>()).Returns((ReceivedEmailResource?)null);

        var result = await _skill.ExecuteAsync(Ctx(), P(Guid.NewGuid()));

        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task AnalyzedEmail_WithExpiredClarification_ShowsItInPlainWords()
    {
        var emailId = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ReceivedEmailResource { Id = emailId, Subject = "Krank" });
        _analysisRepository.GetBySourceAsync(InboundSourceKind.Email, emailId, Arg.Any<CancellationToken>())
            .Returns(new InboundAnalysis
            {
                Id = analysisId,
                SourceKind = InboundSourceKind.Email,
                SourceId = emailId,
                Channel = "Email",
                Intent = EmailIntent.Other,
                Summary = "Unklar",
                AnalyzedAt = new DateTime(2026, 9, 23, 6, 0, 0, DateTimeKind.Utc)
            });
        _clarificationRepository.GetByAnalysisIdAsync(analysisId, Arg.Any<CancellationToken>())
            .Returns(new InboundClarification
            {
                Id = Guid.NewGuid(),
                Question = "Heißt das, du kannst heute nicht arbeiten?",
                Status = InboundClarificationStatus.Expired
            });

        var result = await _skill.ExecuteAsync(Ctx(), P(emailId));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("Heißt das, du kannst heute nicht arbeiten?");
        result.Message.ShouldContain("not answered in time");
        result.Message.ShouldNotContain("Expired");
    }

    [Test]
    public async Task AnalyzedEmail_WithoutClarification_HasNoClarificationSentence()
    {
        var emailId = Guid.NewGuid();
        _mediator.Send(Arg.Any<GetReceivedEmailQuery>(), Arg.Any<CancellationToken>())
            .Returns(new ReceivedEmailResource { Id = emailId, Subject = "Ferien" });
        _analysisRepository.GetBySourceAsync(InboundSourceKind.Email, emailId, Arg.Any<CancellationToken>())
            .Returns(new InboundAnalysis { Id = Guid.NewGuid(), Intent = EmailIntent.VacationRequest, Summary = "Ferien im August" });

        var result = await _skill.ExecuteAsync(Ctx(), P(emailId));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldNotContain("asked the employee back");
    }
}
