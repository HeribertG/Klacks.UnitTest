using FluentValidation.Results;
using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Validation.Clients;
using Klacks.Api.Domain.Interfaces.RouteOptimization;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Services.Common;
using NSubstitute;
using NUnit.Framework;

namespace Klacks.UnitTest.Validation.Clients;

[TestFixture]
public class PutCommandValidatorTests
{
    private IShiftRepository _shiftRepository = null!;
    private PutCommandValidator _validator = null!;

    private const string MembershipCloseErrorKey = "group.validation.membership-close-has-future-works";

    [SetUp]
    public void Setup()
    {
        var geocoding = Substitute.For<IGeocodingService>();
        var countryResolver = Substitute.For<ICountryResolver>();
        var addressRepository = Substitute.For<IAddressRepository>();
        var stateResolver = new StateAbbreviationResolver(Substitute.For<IStateRepository>());
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.HasWorksForClientInGroupAsync(Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        _validator = new PutCommandValidator(geocoding, stateResolver, countryResolver, addressRepository, _shiftRepository);
    }

    private static PutCommand<ClientResource> CommandWithMembership(Guid clientId, Guid groupId, DateTime? validUntil)
    {
        var resource = new ClientResource
        {
            Id = clientId,
            SkipAddressValidation = true,
            GroupItems = new List<ClientGroupItemResource>
            {
                new() { GroupId = groupId, ClientId = clientId, ValidUntil = validUntil }
            }
        };
        return new PutCommand<ClientResource>(resource);
    }

    [Test]
    public async Task MembershipClose_Blocked_WhenWorksExistAfterValidUntil()
    {
        var clientId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        _shiftRepository.HasWorksForClientInGroupAsync(clientId, groupId, Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(true);

        var result = await _validator.ValidateAsync(CommandWithMembership(clientId, groupId, new DateTime(2026, 6, 30)));

        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == MembershipCloseErrorKey));
    }

    [Test]
    public async Task MembershipClose_Allowed_WhenNoWorksAfterValidUntil()
    {
        var clientId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        _shiftRepository.HasWorksForClientInGroupAsync(clientId, groupId, Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>())
            .Returns(false);

        var result = await _validator.ValidateAsync(CommandWithMembership(clientId, groupId, new DateTime(2026, 6, 30)));

        Assert.That(result.Errors, Has.None.Matches<ValidationFailure>(f => f.ErrorMessage == MembershipCloseErrorKey));
    }

    [Test]
    public async Task MembershipClose_QueriesWorksAfterTheChosenValidUntilDate()
    {
        var clientId = Guid.NewGuid();
        var groupId = Guid.NewGuid();
        var validUntil = new DateTime(2026, 6, 30);

        await _validator.ValidateAsync(CommandWithMembership(clientId, groupId, validUntil));

        await _shiftRepository.Received().HasWorksForClientInGroupAsync(
            clientId,
            groupId,
            Arg.Is<DateOnly?>(d => d == DateOnly.FromDateTime(validUntil)),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task OpenMembership_WithoutValidUntil_IsNotChecked()
    {
        var clientId = Guid.NewGuid();
        var groupId = Guid.NewGuid();

        await _validator.ValidateAsync(CommandWithMembership(clientId, groupId, null));

        await _shiftRepository.DidNotReceive().HasWorksForClientInGroupAsync(
            Arg.Any<Guid>(), Arg.Any<Guid>(), Arg.Any<DateOnly?>(), Arg.Any<CancellationToken>());
    }
}
