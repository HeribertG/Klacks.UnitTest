// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Converting a stand-in in place: the same tracked client gets the target type and the person data plus the optional
/// address and communications, keeps id, membership, group items and qualifications, is saved once and its real plan is
/// queued for the thorough recalculation from the membership start to the latest work. A client that is no stand-in is
/// refused, a hidden client is answered like a missing one, and without works nothing is queued.
/// </summary>

using Klacks.Api.Application.Commands.Clients;
using Klacks.Api.Application.DTOs.Settings;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Handlers.Clients;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces.Email;
using Klacks.Api.Domain.Models.Staffs;
using Microsoft.Extensions.Logging;
using Shouldly;

namespace Klacks.UnitTest.Application.Handlers.Clients;

[TestFixture]
public class ConvertStandInCommandHandlerTests
{
    private static readonly DateTime MembershipStart = new(2026, 12, 1, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly LatestWork = new(2026, 12, 20);

    private IClientRepository _clientRepository = null!;
    private IClientVisibilityGuard _clientVisibilityGuard = null!;
    private IUnitOfWork _unitOfWork = null!;
    private IEmailClientAssignmentService _emailAssignmentService = null!;
    private ISurchargeRecalculationScope _recalculationScope = null!;
    private IThoroughRecalculationQueue _recalculationQueue = null!;
    private ConvertStandInCommandHandler _handler = null!;
    private Client _standIn = null!;
    private Guid _groupItemId;
    private Guid _qualificationId;

    [SetUp]
    public void Setup()
    {
        _groupItemId = Guid.NewGuid();
        _qualificationId = Guid.NewGuid();
        _standIn = new Client
        {
            Id = Guid.NewGuid(),
            IdNumber = 4711,
            Type = EntityTypeEnum.StandIn,
            Name = "Fair 3",
            FirstName = string.Empty,
            Membership = new Membership { ValidFrom = MembershipStart },
        };
        _standIn.GroupItems.Add(new GroupItem { Id = _groupItemId, GroupId = Guid.NewGuid(), ClientId = _standIn.Id });
        _standIn.Qualifications.Add(new ClientQualification { Id = _qualificationId, ClientId = _standIn.Id });

        _clientRepository = Substitute.For<IClientRepository>();
        _clientRepository.GetTrackedForUpdate(_standIn.Id).Returns(_standIn);
        _clientVisibilityGuard = Substitute.For<IClientVisibilityGuard>();
        _clientVisibilityGuard.IsVisibleAsync(_standIn.Id, Arg.Any<CancellationToken>()).Returns(true);
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _emailAssignmentService = Substitute.For<IEmailClientAssignmentService>();
        _recalculationScope = Substitute.For<ISurchargeRecalculationScope>();
        _recalculationScope.GetLatestWorkDateAsync(
                Arg.Is<IReadOnlyCollection<Guid>>(ids => ids.Single() == _standIn.Id),
                DateOnly.FromDateTime(MembershipStart),
                Arg.Any<CancellationToken>())
            .Returns(LatestWork);
        _recalculationQueue = Substitute.For<IThoroughRecalculationQueue>();
        _recalculationQueue.QueueRecalculation(
                Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<Guid?>(), Arg.Any<Guid?>(),
                Arg.Any<IReadOnlyCollection<Guid>?>(), Arg.Any<bool>())
            .Returns(true);

        _handler = new ConvertStandInCommandHandler(
            _clientRepository,
            _clientVisibilityGuard,
            new ClientMapper(),
            _unitOfWork,
            _emailAssignmentService,
            _recalculationScope,
            _recalculationQueue,
            Substitute.For<ILogger<ConvertStandInCommandHandler>>());
    }

    private ConvertStandInCommand Command(EntityTypeEnum target = EntityTypeEnum.Employee) => new(_standIn.Id, new ConvertStandInResource
    {
        TargetType = (int)target,
        FirstName = " Anna ",
        Name = "Muster",
        Gender = GenderEnum.Female,
    });

    [TestCase(EntityTypeEnum.Employee)]
    [TestCase(EntityTypeEnum.ExternEmp)]
    public async Task Convert_SetsTypeAndPersonData_KeepsIdMembershipGroupItemsQualifications(EntityTypeEnum target)
    {
        var result = await _handler.Handle(Command(target), CancellationToken.None);

        _standIn.Type.ShouldBe(target);
        _standIn.FirstName.ShouldBe("Anna");
        _standIn.Name.ShouldBe("Muster");
        _standIn.Gender.ShouldBe(GenderEnum.Female);
        _standIn.Membership!.ValidFrom.ShouldBe(MembershipStart);
        _standIn.GroupItems.ShouldHaveSingleItem().Id.ShouldBe(_groupItemId);
        _standIn.Qualifications.ShouldHaveSingleItem().Id.ShouldBe(_qualificationId);
        _standIn.Addresses.ShouldBeEmpty();
        result.Id.ShouldBe(_standIn.Id);
        result.IdNumber.ShouldBe(4711);
        result.Type.ShouldBe((int)target);
        await _unitOfWork.Received(1).CompleteAsync();
        await _emailAssignmentService.DidNotReceive().AssignInboxEmailsToClientsAsync();
    }

    [Test]
    public async Task Convert_WithAddressAndMail_AddsThemAsNewRowsOfTheClient_AndAssignsInboxMails()
    {
        var command = Command();
        command.Resource.Address = new AddressResource
        {
            Id = Guid.NewGuid(),
            Type = AddressTypeEnum.Employee,
            Street = "Bahnhofstrasse 1",
            Zip = "8001",
            City = "Zürich",
            ValidFrom = MembershipStart,
        };
        command.Resource.Communications =
        [
            new CommunicationResource { Id = Guid.NewGuid(), Type = CommunicationTypeEnum.PrivateMail, Value = "anna@example.org" },
        ];

        await _handler.Handle(command, CancellationToken.None);

        var address = _standIn.Addresses.ShouldHaveSingleItem();
        address.Id.ShouldBe(Guid.Empty);
        address.ClientId.ShouldBe(_standIn.Id);
        address.Street.ShouldBe("Bahnhofstrasse 1");
        var communication = _standIn.Communications.ShouldHaveSingleItem();
        communication.Id.ShouldBe(Guid.Empty);
        communication.ClientId.ShouldBe(_standIn.Id);
        communication.Value.ShouldBe("anna@example.org");
        await _emailAssignmentService.Received(1).AssignInboxEmailsToClientsAsync();
    }

    [Test]
    public async Task Convert_QueuesTheThoroughRecalculation_OfTheClientsRealPlan_FromMembershipStartToLatestWork()
    {
        await _handler.Handle(Command(), CancellationToken.None);

        _recalculationQueue.Received(1).QueueRecalculation(
            DateOnly.FromDateTime(MembershipStart),
            LatestWork,
            null,
            null,
            Arg.Is<IReadOnlyCollection<Guid>?>(ids => ids != null && ids.Single() == _standIn.Id),
            false);
    }

    [Test]
    public async Task Convert_WithoutWorks_QueuesNothing()
    {
        _recalculationScope.GetLatestWorkDateAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns((DateOnly?)null);

        await _handler.Handle(Command(), CancellationToken.None);

        _recalculationQueue.DidNotReceiveWithAnyArgs().QueueRecalculation(default, default, default, default);
    }

    [Test]
    public async Task Convert_WhenQueueingFails_TheConversionStillSucceeds()
    {
        _recalculationScope.GetLatestWorkDateAsync(
                Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns<DateOnly?>(_ => throw new TimeoutException("db"));

        var result = await _handler.Handle(Command(), CancellationToken.None);

        result.Type.ShouldBe((int)EntityTypeEnum.Employee);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [TestCase(EntityTypeEnum.Employee)]
    [TestCase(EntityTypeEnum.ExternEmp)]
    [TestCase(EntityTypeEnum.Customer)]
    public async Task Convert_AClientThatIsNoStandIn_IsRefused_AndNothingIsSaved(EntityTypeEnum storedType)
    {
        _standIn.Type = storedType;

        await Should.ThrowAsync<InvalidRequestException>(() => _handler.Handle(Command(), CancellationToken.None));

        _standIn.Type.ShouldBe(storedType);
        _standIn.Name.ShouldBe("Fair 3");
        await _unitOfWork.DidNotReceive().CompleteAsync();
        _recalculationQueue.DidNotReceiveWithAnyArgs().QueueRecalculation(default, default, default, default);
    }

    [Test]
    public async Task Convert_AHiddenClient_IsAnsweredLikeAMissingOne_WithoutLoadingIt()
    {
        _clientVisibilityGuard.IsVisibleAsync(_standIn.Id, Arg.Any<CancellationToken>()).Returns(false);

        await Should.ThrowAsync<KeyNotFoundException>(() => _handler.Handle(Command(), CancellationToken.None));

        await _clientRepository.DidNotReceive().GetTrackedForUpdate(Arg.Any<Guid>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }
}
