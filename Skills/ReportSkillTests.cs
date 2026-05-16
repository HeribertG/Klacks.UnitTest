// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the S9 report skills: list_report_templates, generate_period_summary,
/// email_schedule_to_client.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Reports;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ReportSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewShifts", "CanViewSettings" }
    };

    [Test]
    public async Task ListReportTemplates_NoFilter_ReturnsAll()
    {
        var repo = Substitute.For<IReportTemplateRepository>();
        repo.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<ReportTemplate>
        {
            new() { Id = Guid.NewGuid(), Name = "Monthly", Type = ReportType.Schedule },
            new() { Id = Guid.NewGuid(), Name = "Client overview", Type = ReportType.Client }
        });
        var skill = new ListReportTemplatesSkill(repo);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        Assert.That(result.Success, Is.True);
        await repo.Received(1).GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ListReportTemplates_TypeFilter_DelegatesToGetByTypeAsync()
    {
        var repo = Substitute.For<IReportTemplateRepository>();
        repo.GetByTypeAsync(ReportType.Schedule, Arg.Any<CancellationToken>()).Returns(new List<ReportTemplate>
        {
            new() { Id = Guid.NewGuid(), Name = "Monthly", Type = ReportType.Schedule }
        });
        var skill = new ListReportTemplatesSkill(repo);

        var result = await skill.ExecuteAsync(Ctx(),
            new Dictionary<string, object> { ["type"] = "Schedule" });

        Assert.That(result.Success, Is.True);
        await repo.Received(1).GetByTypeAsync(ReportType.Schedule, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ListReportTemplates_InvalidType_ReturnsError()
    {
        var repo = Substitute.For<IReportTemplateRepository>();
        var skill = new ListReportTemplatesSkill(repo);

        var result = await skill.ExecuteAsync(Ctx(),
            new Dictionary<string, object> { ["type"] = "Quarterly" });

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public async Task GeneratePeriodSummary_ReturnsClientCount()
    {
        var groupId = Guid.NewGuid();
        var repo = Substitute.For<IClientRepository>();
        repo.GetActiveClientsWithAddressesForGroupsAsync(
            Arg.Is<List<Guid>>(l => l.Single() == groupId), Arg.Any<CancellationToken>())
            .Returns(new List<Client>
            {
                new() { Id = Guid.NewGuid(), FirstName = "Anna", Name = "M" },
                new() { Id = Guid.NewGuid(), FirstName = "Max", Name = "M" }
            });
        var skill = new GeneratePeriodSummarySkill(repo);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupId"] = groupId.ToString(),
            ["fromDate"] = "2026-06-01",
            ["untilDate"] = "2026-06-30"
        });

        Assert.That(result.Success, Is.True);
        Assert.That(result.Message, Does.Contain("2 employee"));
    }

    [Test]
    public async Task GeneratePeriodSummary_RejectsInvertedRange()
    {
        var repo = Substitute.For<IClientRepository>();
        var skill = new GeneratePeriodSummarySkill(repo);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["groupId"] = Guid.NewGuid().ToString(),
            ["fromDate"] = "2026-06-30",
            ["untilDate"] = "2026-06-01"
        });

        Assert.That(result.Success, Is.False);
    }

    [Test]
    public async Task EmailScheduleToClient_ReturnsNavigationWhenClientFound()
    {
        var clientId = Guid.NewGuid();
        var repo = Substitute.For<IClientRepository>();
        repo.Get(clientId).Returns(new Client { Id = clientId, FirstName = "Anna", Name = "Müller" });
        var skill = new EmailScheduleToClientSkill(repo);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = clientId.ToString(),
            ["fromDate"] = "2026-06-01"
        });

        Assert.That(result.Success, Is.True);
        Assert.That(result.Type, Is.EqualTo(SkillResultType.Navigation));
    }

    [Test]
    public async Task EmailScheduleToClient_ErrorsWhenClientMissing()
    {
        var clientId = Guid.NewGuid();
        var repo = Substitute.For<IClientRepository>();
        repo.Get(clientId).Returns((Client?)null);
        var skill = new EmailScheduleToClientSkill(repo);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["clientId"] = clientId.ToString()
        });

        Assert.That(result.Success, Is.False);
    }
}
