// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.DTOs.Registrations;
using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Accounts;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Mediator;
using Microsoft.AspNetCore.Identity;

namespace Klacks.UnitTest.Handlers.Accounts;

[TestFixture]
public class CompleteOwnAdminSetupCommandHandlerTests
{
    private IAdminSetupGateService _gateService = null!;
    private IUserManagementService _userManagementService = null!;
    private IUnitOfWork _unitOfWork = null!;
    private CompleteOwnAdminSetupCommandHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _gateService = Substitute.For<IAdminSetupGateService>();
        _userManagementService = Substitute.For<IUserManagementService>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.ArgAt<Func<Task<bool>>>(0)());

        _sut = new CompleteOwnAdminSetupCommandHandler(
            _gateService,
            _userManagementService,
            new AuthMapper(),
            _unitOfWork,
            Substitute.For<Microsoft.Extensions.Logging.ILogger<CompleteOwnAdminSetupCommandHandler>>());
    }

    private static CompleteOwnAdminSetupCommand Cmd() => new(new RegistrationResource
    {
        Email = "new-admin@example.com",
        FirstName = "New",
        LastName = "Admin",
        Password = "Password1!",
        UserName = "new-admin",
    });

    [Test]
    public async Task Handle_ExemptEnvironment_ThrowsWithEnvironmentMessage()
    {
        _gateService.IsExemptAsync().Returns(true);

        var ex = await Should.ThrowAsync<ConflictException>(
            async () => await _sut.Handle(Cmd(), CancellationToken.None));

        ex.Message.ShouldBe(AdminSetupMessages.NotRequiredInEnvironment);
        await _gateService.DidNotReceive().IsGateActiveAsync();
        await _userManagementService.DidNotReceive().RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>());
    }

    [Test]
    public async Task Handle_NotExemptButGateInactive_ThrowsAlreadyCompleted()
    {
        _gateService.IsExemptAsync().Returns(false);
        _gateService.IsGateActiveAsync().Returns(false);

        var ex = await Should.ThrowAsync<ConflictException>(
            async () => await _sut.Handle(Cmd(), CancellationToken.None));

        ex.Message.ShouldBe(AdminSetupMessages.AlreadyCompleted);
        await _userManagementService.DidNotReceive().RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>());
    }

    [Test]
    public async Task Handle_GateActive_RegistersNewAdminAndDeactivatesSeedAdmin()
    {
        _gateService.IsExemptAsync().Returns(false);
        _gateService.IsGateActiveAsync().Returns(true);
        _userManagementService.RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>())
            .Returns((true, (IdentityResult?)null));
        _userManagementService.ChangeUserRoleAsync(Arg.Any<string>(), Roles.Admin, true)
            .Returns((true, string.Empty));
        _userManagementService.DeactivateUserAsync(Arg.Any<Guid>(), Arg.Any<string>())
            .Returns((true, string.Empty));

        var result = await _sut.Handle(Cmd(), CancellationToken.None);

        result.ShouldBeTrue();
        await _userManagementService.Received(1).RegisterUserAsync(Arg.Any<AppUser>(), "Password1!");
    }
}
