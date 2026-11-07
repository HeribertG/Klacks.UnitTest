// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Creating stand-ins in bulk: N stand-ins with the pattern name from the next free index, each with a membership and a
/// group membership for the period and the qualifications at StandInConstants.QualificationLevel, staged and saved with
/// one save; a hidden or missing group is answered like a missing one and an unknown qualification refuses the request,
/// both before anything is staged.
/// </summary>

using Klacks.Api.Application.Commands.Clients;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Handlers.Clients;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Models.Staffs;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Klacks.UnitTest.Application.Handlers.Clients;

[TestFixture]
public class CreateStandInsCommandHandlerTests
{
    private static readonly DateOnly From = new(2026, 12, 1);
    private static readonly DateOnly Until = new(2026, 12, 31);

    private IClientRepository _clientRepository = null!;
    private IGroupRepository _groupRepository = null!;
    private IQualificationRepository _qualificationRepository = null!;
    private IGroupVisibilityGuard _groupVisibilityGuard = null!;
    private IUnitOfWork _unitOfWork = null!;
    private CreateStandInsCommandHandler _handler = null!;
    private readonly List<Client> _staged = [];
    private readonly Guid _groupId = Guid.NewGuid();
    private readonly Guid _qualificationId = Guid.NewGuid();

    [SetUp]
    public void Setup()
    {
        _staged.Clear();
        _clientRepository = Substitute.For<IClientRepository>();
        _clientRepository.Add(Arg.Do<Client>(_staged.Add)).Returns(Task.CompletedTask);
        _clientRepository.ListNamesInFrameAsync("Fair ", string.Empty, Arg.Any<CancellationToken>())
            .Returns(["Fair 1", "Fair 2", "Fair 5"]);
        _groupRepository = Substitute.For<IGroupRepository>();
        _groupRepository.Exists(_groupId).Returns(true);
        _qualificationRepository = Substitute.For<IQualificationRepository>();
        _qualificationRepository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns([new Qualification { Id = _qualificationId }]);
        _groupVisibilityGuard = Substitute.For<IGroupVisibilityGuard>();
        _groupVisibilityGuard.IsGroupVisibleAsync(_groupId, Arg.Any<CancellationToken>()).Returns(true);
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _handler = new CreateStandInsCommandHandler(
            _clientRepository,
            _groupRepository,
            _qualificationRepository,
            _groupVisibilityGuard,
            _unitOfWork,
            Substitute.For<ILogger<CreateStandInsCommandHandler>>());
    }

    private CreateStandInsCommand Command(int count = 3, List<Guid>? qualificationIds = null) => new(new CreateStandInsResource
    {
        Count = count,
        NamePattern = "Fair {n}",
        GroupId = _groupId,
        ValidFrom = From,
        ValidUntil = Until,
        QualificationIds = qualificationIds ?? [_qualificationId],
    });

    [Test]
    public async Task Creates_N_With_Pattern_Group_Period_Qualifications()
    {
        var created = await _handler.Handle(Command(), CancellationToken.None);

        _staged.Select(client => client.Name).ShouldBe(["Fair 6", "Fair 7", "Fair 8"]);
        created.Select(resource => resource.Name).ShouldBe(["Fair 6", "Fair 7", "Fair 8"]);
        var expectedFrom = From.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        var expectedUntil = Until.ToDateTime(TimeOnly.MinValue, DateTimeKind.Utc);
        foreach (var standIn in _staged)
        {
            standIn.Type.ShouldBe(EntityTypeEnum.StandIn);
            standIn.FirstName.ShouldBe(string.Empty);
            standIn.Addresses.ShouldBeEmpty();
            standIn.Communications.ShouldBeEmpty();
            standIn.ClientContracts.ShouldBeEmpty();
            standIn.Membership.ShouldNotBeNull();
            standIn.Membership!.ValidFrom.ShouldBe(expectedFrom);
            standIn.Membership.ValidFrom.Kind.ShouldBe(DateTimeKind.Utc);
            standIn.Membership.ValidUntil.ShouldBe(expectedUntil);
            var groupItem = standIn.GroupItems.ShouldHaveSingleItem();
            groupItem.GroupId.ShouldBe(_groupId);
            groupItem.ShiftId.ShouldBeNull();
            groupItem.ValidFrom.ShouldBe(expectedFrom);
            groupItem.ValidUntil.ShouldBe(expectedUntil);
            var qualification = standIn.Qualifications.ShouldHaveSingleItem();
            qualification.QualificationId.ShouldBe(_qualificationId);
            qualification.Level.ShouldBe(StandInConstants.QualificationLevel);
        }

        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task WithoutQualifications_CreatesNone_AndReadsNoQualification()
    {
        await _handler.Handle(Command(count: 1, qualificationIds: []), CancellationToken.None);

        _staged.ShouldHaveSingleItem().Qualifications.ShouldBeEmpty();
        await _qualificationRepository.DidNotReceive().GetAllAsync(Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HiddenGroup_IsAnsweredLikeAMissingOne_AndNothingIsStaged()
    {
        _groupVisibilityGuard.IsGroupVisibleAsync(_groupId, Arg.Any<CancellationToken>()).Returns(false);

        await Should.ThrowAsync<KeyNotFoundException>(() => _handler.Handle(Command(), CancellationToken.None));

        _staged.ShouldBeEmpty();
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task MissingGroup_IsNotFound_AndNothingIsStaged()
    {
        _groupRepository.Exists(_groupId).Returns(false);

        await Should.ThrowAsync<KeyNotFoundException>(() => _handler.Handle(Command(), CancellationToken.None));

        _staged.ShouldBeEmpty();
    }

    [Test]
    public async Task UnknownQualification_IsRefused_AndNothingIsStaged()
    {
        var unknown = Guid.NewGuid();

        var exception = await Should.ThrowAsync<InvalidRequestException>(
            () => _handler.Handle(Command(qualificationIds: [_qualificationId, unknown]), CancellationToken.None));

        exception.Message.ShouldContain(unknown.ToString());
        _staged.ShouldBeEmpty();
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task IndexBeyondInt_IsRefused_AndNothingIsStaged()
    {
        _clientRepository.ListNamesInFrameAsync("Fair ", string.Empty, Arg.Any<CancellationToken>())
            .Returns([$"Fair {int.MaxValue - 1}"]);

        await Should.ThrowAsync<InvalidRequestException>(() => _handler.Handle(Command(count: 2), CancellationToken.None));

        _staged.ShouldBeEmpty();
    }
}
