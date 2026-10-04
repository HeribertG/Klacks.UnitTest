// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the get_messenger_analysis skill — the messenger counterpart of
/// get_email_analysis, backed by the same channel-neutral IInboundAnalysisRepository. A message attributed to a
/// hidden client and a message attributed to no client at all (for a non-admin) are both answered like a
/// message that was never analyzed.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
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
    private IClientVisibilityGuard _clientVisibilityGuard = null!;

    [SetUp]
    public void Setup()
    {
        _analysisRepository = Substitute.For<IInboundAnalysisRepository>();
        _clarificationRepository = Substitute.For<IInboundClarificationRepository>();
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _clientVisibilityGuard.IsVisibleAsync(Arg.Any<Guid>(), Arg.Any<CancellationToken>()).Returns(true);
    }

    private static SkillExecutionContext Ctx(params string[] rights) => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = rights.Length == 0
            ? new List<string> { Roles.Authorised, Permissions.CanManageAutomation }
            : rights.ToList()
    };

    private static InboundAnalysis UnattributedAnalysis(Guid messageId) => new()
    {
        SourceKind = InboundSourceKind.Messenger,
        SourceId = messageId,
        Channel = "Messenger:Telegram",
        ClientId = null,
        Intent = EmailIntent.WorkCancellation,
        Summary = "Is sick today and cannot come",
        AnalyzedAt = new DateTime(2026, 10, 4, 6, 0, 0, DateTimeKind.Utc)
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
                ClientId = Guid.NewGuid(),
                Intent = EmailIntent.VacationRequest,
                Summary = "Wants two weeks off in July",
                AnalyzedAt = new DateTime(2026, 7, 10, 6, 0, 0, DateTimeKind.Utc)
            });
        var skill = new GetMessengerAnalysisSkill(_analysisRepository, _clarificationRepository, _clientVisibilityGuard);

        var result = await skill.ExecuteAsync(Ctx(), P(id));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("VacationRequest");
        result.Message.ShouldContain("Wants two weeks off in July");
    }

    [Test]
    public async Task GetMessengerAnalysis_ClientHiddenFromCaller_AnsweredLikeNotAnalyzed()
    {
        var id = Guid.NewGuid();
        var hiddenClientId = Guid.NewGuid();
        _analysisRepository.GetBySourceAsync(InboundSourceKind.Messenger, id, Arg.Any<CancellationToken>())
            .Returns(new InboundAnalysis
            {
                SourceKind = InboundSourceKind.Messenger,
                SourceId = id,
                ClientId = hiddenClientId,
                Intent = EmailIntent.VacationRequest,
                Summary = "Wants two weeks off in July",
                AnalyzedAt = new DateTime(2026, 7, 10, 6, 0, 0, DateTimeKind.Utc)
            });
        _clientVisibilityGuard.IsVisibleAsync(hiddenClientId, Arg.Any<CancellationToken>()).Returns(false);
        var skill = new GetMessengerAnalysisSkill(_analysisRepository, _clarificationRepository, _clientVisibilityGuard);

        var result = await skill.ExecuteAsync(Ctx(), P(id));

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("not been analyzed");
        result.Message.ShouldNotContain("Wants two weeks off in July");
        await _clarificationRepository.DidNotReceiveWithAnyArgs().GetByAnalysisIdAsync(default, default);
    }

    [Test]
    public async Task GetMessengerAnalysis_SaysSo_WhenNotAnalyzed()
    {
        var id = Guid.NewGuid();
        _analysisRepository.GetBySourceAsync(InboundSourceKind.Messenger, id, Arg.Any<CancellationToken>())
            .Returns((InboundAnalysis?)null);
        var skill = new GetMessengerAnalysisSkill(_analysisRepository, _clarificationRepository, _clientVisibilityGuard);

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
                ClientId = Guid.NewGuid(),
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
        var skill = new GetMessengerAnalysisSkill(_analysisRepository, _clarificationRepository, _clientVisibilityGuard);

        var result = await skill.ExecuteAsync(Ctx(), P(id));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("Kannst du heute nicht arbeiten?");
        result.Message.ShouldContain("waiting for the employee's answer");
    }

    [Test]
    public async Task GetMessengerAnalysis_UnattributedMessage_AnsweredLikeNotAnalyzed_ForSupervisor()
    {
        var id = Guid.NewGuid();
        _analysisRepository.GetBySourceAsync(InboundSourceKind.Messenger, id, Arg.Any<CancellationToken>())
            .Returns(UnattributedAnalysis(id));
        var skill = new GetMessengerAnalysisSkill(_analysisRepository, _clarificationRepository, _clientVisibilityGuard);

        var result = await skill.ExecuteAsync(Ctx(Roles.Authorised, Permissions.CanManageAutomation), P(id));

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("not been analyzed");
        result.Message.ShouldNotContain("Is sick today");
        await _clarificationRepository.DidNotReceiveWithAnyArgs().GetByAnalysisIdAsync(default, default);
    }

    [Test]
    public async Task GetMessengerAnalysis_UnattributedMessage_ShownToAdmin()
    {
        var id = Guid.NewGuid();
        _analysisRepository.GetBySourceAsync(InboundSourceKind.Messenger, id, Arg.Any<CancellationToken>())
            .Returns(UnattributedAnalysis(id));
        var skill = new GetMessengerAnalysisSkill(_analysisRepository, _clarificationRepository, _clientVisibilityGuard);

        var result = await skill.ExecuteAsync(Ctx(Roles.Admin, Permissions.CanManageAutomation), P(id));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("Is sick today");
    }
}
