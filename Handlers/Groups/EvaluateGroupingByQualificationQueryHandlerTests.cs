// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for EvaluateGroupingByQualificationQueryHandler: only qualifications valid today (company
/// clock) count, matching fill_group_by_criteria's own validity semantics; qualifications overlap so a
/// client can contribute to several buckets; a qualification whose display name already matches an
/// existing group is reported as already covered instead of proposed as a duplicate.
/// </summary>

using Klacks.Api.Application.Handlers.Groups;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.Groups;
using Klacks.Api.Application.Queries.Qualifications;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Handlers.Groups;

[TestFixture]
public class EvaluateGroupingByQualificationQueryHandlerTests
{
    private static readonly DateTime CompanyToday = new(2026, 3, 15, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = DateOnly.FromDateTime(CompanyToday);

    private static readonly Guid FirstAidId = Guid.NewGuid();
    private static readonly Guid ForkliftId = Guid.NewGuid();

    private IClientRepository _clientRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IMediator _mediator = null!;
    private ICompanyClock _companyClock = null!;
    private EvaluateGroupingByQualificationQueryHandler _handler = null!;

    [SetUp]
    public void Setup()
    {
        _clientRepository = Substitute.For<IClientRepository>();
        _groupRepository = Substitute.For<IGroupRepository>();
        _mediator = Substitute.For<IMediator>();
        _companyClock = Substitute.For<ICompanyClock>();

        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(CompanyToday);
        _groupRepository.List().Returns(new List<Group>());
        _mediator.Send(Arg.Any<ListQuery>(), Arg.Any<CancellationToken>())
            .Returns(new List<Qualification>
            {
                new() { Id = FirstAidId, Name = new MultiLanguage { De = "Erste Hilfe" } },
                new() { Id = ForkliftId, Name = new MultiLanguage { De = "Stapler" } }
            });

        _handler = new EvaluateGroupingByQualificationQueryHandler(
            _clientRepository, _groupRepository, _mediator, _companyClock);
    }

    private static ClientQualification Valid(Guid qualificationId) => new()
    {
        QualificationId = qualificationId,
        Level = QualificationLevel.Basic,
        ValidFrom = Today.AddDays(-30),
        ValidUntil = null
    };

    private static Client ClientWithQualifications(params ClientQualification[] qualifications) => new()
    {
        Id = Guid.NewGuid(),
        FirstName = "Test",
        Name = "Client",
        Type = EntityTypeEnum.Employee,
        Qualifications = qualifications.ToList()
    };

    private void SetClients(params Client[] clients)
    {
        _clientRepository.GetByTypeWithQualificationsAsync(Arg.Any<EntityTypeEnum>(), Arg.Any<CancellationToken>())
            .Returns(clients.ToList());
    }

    private static EvaluateGroupingByQualificationQuery Query() => new(EntityTypeEnum.Employee);

    [Test]
    public async Task QualificationAtOrAboveThreshold_IsListedAsCandidate()
    {
        SetClients(
            ClientWithQualifications(Valid(FirstAidId)),
            ClientWithQualifications(Valid(FirstAidId)),
            ClientWithQualifications(Valid(FirstAidId)));

        var result = await _handler.Handle(Query(), CancellationToken.None);

        Assert.That(result.Candidates, Has.Count.EqualTo(1));
        Assert.That(result.Candidates[0].QualificationId, Is.EqualTo(FirstAidId));
        Assert.That(result.Candidates[0].QualificationName, Is.EqualTo("Erste Hilfe"));
        Assert.That(result.Candidates[0].ClientCount, Is.EqualTo(3));
        Assert.That(result.Candidates[0].IsViable, Is.True);
    }

    [Test]
    public async Task QualificationBelowThreshold_IsListedAsNearThreshold()
    {
        SetClients(ClientWithQualifications(Valid(FirstAidId)), ClientWithQualifications(Valid(FirstAidId)));

        var result = await _handler.Handle(Query(), CancellationToken.None);

        Assert.That(result.Candidates, Is.Empty);
        Assert.That(result.NearThresholdCandidates, Has.Count.EqualTo(1));
        Assert.That(result.NearThresholdCandidates[0].IsViable, Is.False);
    }

    [Test]
    public async Task QualificationMatchingExistingGroupName_IsReportedAsAlreadyCovered()
    {
        _groupRepository.List().Returns(new List<Group> { new() { Name = "Erste Hilfe" } });
        SetClients(
            ClientWithQualifications(Valid(FirstAidId)),
            ClientWithQualifications(Valid(FirstAidId)),
            ClientWithQualifications(Valid(FirstAidId)));

        var result = await _handler.Handle(Query(), CancellationToken.None);

        Assert.That(result.Candidates, Is.Empty);
        Assert.That(result.NearThresholdCandidates, Is.Empty);
        Assert.That(result.QualificationsAlreadyCovered, Has.Count.EqualTo(1));
        Assert.That(result.QualificationsAlreadyCovered[0], Is.EqualTo("Erste Hilfe"));
    }

    [Test]
    public async Task ExpiredQualification_DoesNotCount()
    {
        var expired = new ClientQualification
        {
            QualificationId = FirstAidId,
            Level = QualificationLevel.Basic,
            ValidFrom = Today.AddDays(-60),
            ValidUntil = Today.AddDays(-1)
        };
        SetClients(ClientWithQualifications(expired));

        var result = await _handler.Handle(Query(), CancellationToken.None);

        Assert.That(result.Candidates, Is.Empty);
        Assert.That(result.NearThresholdCandidates, Is.Empty);
        Assert.That(result.ClientsWithoutValidQualification, Is.EqualTo(1));
    }

    [Test]
    public async Task NotYetValidQualification_DoesNotCount()
    {
        var future = new ClientQualification
        {
            QualificationId = FirstAidId,
            Level = QualificationLevel.Basic,
            ValidFrom = Today.AddDays(10),
            ValidUntil = null
        };
        SetClients(ClientWithQualifications(future));

        var result = await _handler.Handle(Query(), CancellationToken.None);

        Assert.That(result.ClientsWithoutValidQualification, Is.EqualTo(1));
    }

    [Test]
    public async Task DeletedQualificationRow_DoesNotCount()
    {
        var deleted = Valid(FirstAidId);
        deleted.IsDeleted = true;
        SetClients(ClientWithQualifications(deleted));

        var result = await _handler.Handle(Query(), CancellationToken.None);

        Assert.That(result.ClientsWithoutValidQualification, Is.EqualTo(1));
    }

    [Test]
    public async Task ClientWithMultipleQualifications_ContributesToEachBucket_OverlapNotPartition()
    {
        SetClients(
            ClientWithQualifications(Valid(FirstAidId), Valid(ForkliftId)),
            ClientWithQualifications(Valid(FirstAidId)),
            ClientWithQualifications(Valid(ForkliftId)));

        var result = await _handler.Handle(Query(), CancellationToken.None);

        Assert.That(result.TotalClientsEvaluated, Is.EqualTo(3));
        var firstAid = result.NearThresholdCandidates.Single(c => c.QualificationId == FirstAidId);
        var forklift = result.NearThresholdCandidates.Single(c => c.QualificationId == ForkliftId);
        Assert.That(firstAid.ClientCount, Is.EqualTo(2));
        Assert.That(forklift.ClientCount, Is.EqualTo(2));
    }
}
