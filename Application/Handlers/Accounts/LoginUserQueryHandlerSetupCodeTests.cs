// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins the sign-in refusal that keeps the one-time setup code from being bypassed: when the gate service reserves the
/// seeded admin for the setup code, the sign-in is refused with the ordinary "invalid e-mail or password" answer before
/// the password is checked (so Identity never rehashes the seeded hash); otherwise the sign-in proceeds as before, with
/// the request origin passed on unchanged.
/// </summary>

using Klacks.Api.Application.Exceptions;
using Klacks.Api.Application.Handlers.Accounts;
using Klacks.Api.Application.Queries.Accounts;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Authentification;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Accounts;

[TestFixture]
public class LoginUserQueryHandlerSetupCodeTests
{
    private const string SeedEmail = "admin@test.com";
    private const string DefaultPassword = "P@ssw0rt1";

    private IAccountAuthenticationService _authenticationService = null!;
    private IAdminSetupGateService _gateService = null!;
    private LoginUserQueryHandler _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _authenticationService = Substitute.For<IAccountAuthenticationService>();
        _gateService = Substitute.For<IAdminSetupGateService>();
        _sut = new LoginUserQueryHandler(
            _authenticationService, _gateService, Substitute.For<ILogger<LoginUserQueryHandler>>());
    }

    [Test]
    public async Task ReservedSeedSignIn_IsRefusedBeforeThePasswordIsChecked()
    {
        _gateService.IsSeedAdminSignInReservedForSetupCodeAsync(SeedEmail, false).Returns(true);

        var ex = await Should.ThrowAsync<UnauthorizedException>(
            async () => await _sut.Handle(new LoginUserQuery(SeedEmail, DefaultPassword, IsLocalRequest: false), CancellationToken.None));

        ex.Message.ShouldBe("Invalid e-mail address or password.");
        await _authenticationService.DidNotReceive().LogInUserAsync(Arg.Any<string>(), Arg.Any<string>());
    }

    [Test]
    public async Task UnreservedSignIn_ProceedsAndPassesTheRequestOrigin()
    {
        _gateService.IsSeedAdminSignInReservedForSetupCodeAsync(SeedEmail, true).Returns(false);
        _authenticationService.LogInUserAsync(SeedEmail, DefaultPassword).Returns(new AuthenticatedResult
        {
            Success = true,
            Token = "token",
            RefreshToken = "refresh",
            Id = Guid.NewGuid().ToString(),
            Permissions = [],
        });

        var result = await _sut.Handle(new LoginUserQuery(SeedEmail, DefaultPassword, IsLocalRequest: true), CancellationToken.None);

        result.Success.ShouldBeTrue();
        await _gateService.Received(1).IsSeedAdminSignInReservedForSetupCodeAsync(SeedEmail, true);
    }
}
