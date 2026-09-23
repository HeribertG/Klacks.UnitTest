// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the get_messenger_analysis skill — the messenger counterpart of
/// get_email_analysis, backed by the same channel-neutral IInboundAnalysisRepository.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Inbound;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Inbound;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetMessengerAnalysisSkillTests
{
    private IInboundAnalysisRepository _analysisRepository = null!;
    private IInboundClarificationRepository _clarificationRepository = null!;

    [SetUp]
    public void Setup()
    {
        _analysisRepository = Substitute.For<IInboundAnalysisRepository>();
        _clarificationRepository = Substitute.For<IInboundClarificationRepository>();
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewSettings" }
    };

    private static Dictionary<string, object> P(Guid messageId) => new() { ["messageId"] = messageId.ToString() };

    [Test]
    public async Task GetMessengerAnalysis_ReportsIntentAndSummary()
    {
        var id = Guid.NewGuid();
        _analysisRepository.GetBySourceAsync(InboundSourceKind.Messenger, id, Arg.Any<CancellationToken>())
            .Returns(new InboundAnalysis
            {
                SourceKind = InboundSourceKind.Messenger,
                SourceId = id,
                Channel = "Messenger:Telegram",
                Intent = EmailIntent.VacationRequest,
                Summary = "Wants two weeks off in July",
                AnalyzedAt = new DateTime(2026, 7, 10, 6, 0, 0, DateTimeKind.Utc)
            });
        var skill = new GetMessengerAnalysisSkill(_analysisRepository, _clarificationRepository);

        var result = await skill.ExecuteAsync(Ctx(), P(id));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("VacationRequest");
        result.Message.ShouldContain("Wants two weeks off in July");
    }

    [Test]
    public async Task GetMessengerAnalysis_SaysSo_WhenNotAnalyzed()
    {
        var id = Guid.NewGuid();
        _analysisRepository.GetBySourceAsync(InboundSourceKind.Messenger, id, Arg.Any<CancellationToken>())
            .Returns((InboundAnalysis?)null);
        var skill = new GetMessengerAnalysisSkill(_analysisRepository, _clarificationRepository);

        var result = await skill.ExecuteAsync(Ctx(), P(id));

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("not been analyzed");
    }

    [Test]
    public async Task GetMessengerAnalysis_ShowsTheRelatedClarification()
    {
        var id = Guid.NewGuid();
        var analysisId = Guid.NewGuid();
        _analysisRepository.GetBySourceAsync(InboundSourceKind.Messenger, id, Arg.Any<CancellationToken>())
            .Returns(new InboundAnalysis
            {
                Id = analysisId,
                SourceKind = InboundSourceKind.Messenger,
                SourceId = id,
                Channel = "Messenger:Telegram",
                Intent = EmailIntent.Other,
                Summary = "Fühlt sich nicht gut",
                AnalyzedAt = new DateTime(2026, 9, 23, 6, 0, 0, DateTimeKind.Utc)
            });
        _clarificationRepository.GetByAnalysisIdAsync(analysisId, Arg.Any<CancellationToken>())
            .Returns(new InboundClarification
            {
                Id = Guid.NewGuid(),
                Question = "Kannst du heute nicht arbeiten?",
                Status = InboundClarificationStatus.Open
            });
        var skill = new GetMessengerAnalysisSkill(_analysisRepository, _clarificationRepository);

        var result = await skill.ExecuteAsync(Ctx(), P(id));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("Kannst du heute nicht arbeiten?");
        result.Message.ShouldContain("waiting for the employee's answer");
    }
}
