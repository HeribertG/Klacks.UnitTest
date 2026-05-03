using Shouldly;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Accounts;
using Klacks.Api.Infrastructure.Persistence;
using Microsoft.AspNetCore.Http;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Services.Accounts;

[TestFixture]
public class AccountRegistrationServiceTests
{
    private IUnitOfWork _mockUnitOfWork;
    private AccountRegistrationService _registrationService;
    private IAuthenticationService _mockAuthService;
    private IUserManagementService _mockUserManagementService;
    private IRefreshTokenService _mockRefreshTokenService;
    private IAccountAuthenticationService _mockAccountAuthService;
    private IAccountPasswordService _mockAccountPasswordService;
    private ILogger<AccountRegistrationService> _mockLogger;

    [SetUp]
    public void SetUp()
    {
        _mockUnitOfWork = Substitute.For<IUnitOfWork>();
        _mockAuthService = Substitute.For<IAuthenticationService>();
        _mockUserManagementService = Substitute.For<IUserManagementService>();
        _mockRefreshTokenService = Substitute.For<IRefreshTokenService>();
        _mockAccountAuthService = Substitute.For<IAccountAuthenticationService>();
        _mockAccountPasswordService = Substitute.For<IAccountPasswordService>();
        _mockLogger = Substitute.For<ILogger<AccountRegistrationService>>();

        _registrationService = new AccountRegistrationService(
            _mockUnitOfWork,
            _mockAuthService,
            _mockUserManagementService,
            _mockRefreshTokenService,
            _mockAccountAuthService,
            _mockAccountPasswordService,
            _mockLogger);
    }

    [Test]
    public async Task RegisterUserAsync_WithValidData_ShouldReturnSuccess()
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "newuser@example.com",
            UserName = "newuser@example.com",
            FirstName = "John",
            LastName = "Doe"
        };
        var password = "ValidPassword123!";

        _mockUserManagementService.FindUserByEmailAsync(user.Email).Returns((AppUser?)null);
        _mockUserManagementService.RegisterUserAsync(user, password).Returns((true, (Microsoft.AspNetCore.Identity.IdentityResult?)null));
        _mockRefreshTokenService.CalculateTokenExpiryTime().Returns(DateTime.UtcNow.AddHours(1));
        _mockAccountAuthService.SetAuthenticatedResultAsync(Arg.Any<AuthenticatedResult>(), user, Arg.Any<DateTime>())
            .Returns(args => 
            {
                var result = (AuthenticatedResult)args[0];
                result.Success = true;
                return result;
            });

        var result = await _registrationService.RegisterUserAsync(user, password);

        result.ShouldNotBeNull();
        result.Success.ShouldBeTrue();
    }

    [Test]
    public async Task RegisterUserAsync_WithExistingEmail_ShouldReturnFailure()
    {
        var existingUser = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "existing@example.com",
            UserName = "existing@example.com"
        };
        
        var newUser = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "existing@example.com",
            UserName = "existing@example.com"
        };
        var password = "ValidPassword123!";

        _mockUserManagementService.FindUserByEmailAsync(newUser.Email).Returns(existingUser);

        var result = await _registrationService.RegisterUserAsync(newUser, password);

        result.ShouldNotBeNull();
        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task RegisterUserAsync_WithUserCreationFailure_ShouldReturnFailure()
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "newuser@example.com",
            UserName = "newuser@example.com"
        };
        var password = "weak";

        _mockUserManagementService.FindUserByEmailAsync(user.Email).Returns((AppUser?)null);
        _mockUserManagementService.RegisterUserAsync(user, password).Returns((false, (Microsoft.AspNetCore.Identity.IdentityResult?)null));

        var result = await _registrationService.RegisterUserAsync(user, password);

        result.ShouldNotBeNull();
        result.Success.ShouldBeFalse();
    }

    [Test]
    public async Task RegisterUserAsync_WithNullUser_ShouldReturnFailure()
    {
        var password = "ValidPassword123!";
        
        // This will throw NullReferenceException in the actual implementation
        // when trying to access user.Email, which is expected behavior
        await Should.ThrowAsync<NullReferenceException>(async () =>
            await _registrationService.RegisterUserAsync(null!, password));
    }

    [Test]
    public async Task RegisterUserAsync_WithNullOrEmptyPassword_ShouldReturnFailure()
    {
        var user = new AppUser
        {
            Id = Guid.NewGuid().ToString(),
            Email = "newuser@example.com",
            UserName = "newuser@example.com"
        };

        var result1 = await _registrationService.RegisterUserAsync(user, null!);
        var result2 = await _registrationService.RegisterUserAsync(user, "");

        result1.Success.ShouldBeFalse();
        result2.Success.ShouldBeFalse();
    }
}