using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Services.Authentication;
using Klacks.Api.Application.Validation.Accounts;
using Klacks.Api.Domain.Interfaces.Authentification;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Services.Authentication;

[TestFixture]
internal class AuthenticationServiceLockoutTests
{
    private const string Email = "user@test.com";
    private const string Password = "P@ssw0rt1";

    private UserManager<AppUser> _userManager = null!;
    private IIdentityProviderRepository _identityProviderRepository = null!;
    private ILdapService _ldapService = null!;
    private IUsernameGeneratorService _usernameGenerator = null!;
    private AuthenticationService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _userManager = CreateUserManager();
        _identityProviderRepository = Substitute.For<IIdentityProviderRepository>();
        _ldapService = Substitute.For<ILdapService>();
        _usernameGenerator = Substitute.For<IUsernameGeneratorService>();

        // No identity providers configured, so the LDAP fallback always fails.
        _identityProviderRepository.GetAuthenticationProviders()
            .Returns(new List<IdentityProvider>());

        var jwtValidator = new JwtValidator(new JwtSettings());
        var logger = Substitute.For<ILogger<AuthenticationService>>();

        _sut = new AuthenticationService(
            _userManager,
            jwtValidator,
            _identityProviderRepository,
            _ldapService,
            _usernameGenerator,
            logger);
    }

    [TearDown]
    public void TearDown()
    {
        _userManager?.Dispose();
    }

    [Test]
    public async Task ValidateCredentials_WhenUserIsLockedOut_IsRejectedWithoutCheckingPassword()
    {
        var user = new AppUser { Id = "1", Email = Email };
        _userManager.FindByEmailAsync(Email).Returns(user);
        _userManager.HasPasswordAsync(user).Returns(true);
        _userManager.IsLockedOutAsync(user).Returns(true);

        var (isValid, resultUser) = await _sut.ValidateCredentialsAsync(Email, Password);

        isValid.ShouldBeFalse();
        resultUser.ShouldBeNull();
        await _userManager.DidNotReceive().CheckPasswordAsync(user, Arg.Any<string>());
        await _userManager.DidNotReceive().AccessFailedAsync(user);
    }

    [Test]
    public async Task ValidateCredentials_WhenPasswordIsWrong_IncrementsFailedAccessCount()
    {
        var user = new AppUser { Id = "2", Email = Email };
        _userManager.FindByEmailAsync(Email).Returns(user);
        _userManager.HasPasswordAsync(user).Returns(true);
        _userManager.IsLockedOutAsync(user).Returns(false);
        _userManager.CheckPasswordAsync(user, Password).Returns(false);
        _userManager.AccessFailedAsync(user).Returns(IdentityResult.Success);

        var (isValid, resultUser) = await _sut.ValidateCredentialsAsync(Email, Password);

        isValid.ShouldBeFalse();
        resultUser.ShouldBeNull();
        await _userManager.Received(1).AccessFailedAsync(user);
        await _userManager.DidNotReceive().ResetAccessFailedCountAsync(user);
    }

    [Test]
    public async Task ValidateCredentials_WhenPasswordIsCorrect_ResetsFailedAccessCount()
    {
        var user = new AppUser { Id = "3", Email = Email };
        _userManager.FindByEmailAsync(Email).Returns(user);
        _userManager.HasPasswordAsync(user).Returns(true);
        _userManager.IsLockedOutAsync(user).Returns(false);
        _userManager.CheckPasswordAsync(user, Password).Returns(true);
        _userManager.ResetAccessFailedCountAsync(user).Returns(IdentityResult.Success);

        var (isValid, resultUser) = await _sut.ValidateCredentialsAsync(Email, Password);

        isValid.ShouldBeTrue();
        resultUser.ShouldBe(user);
        await _userManager.Received(1).ResetAccessFailedCountAsync(user);
        await _userManager.DidNotReceive().AccessFailedAsync(user);
    }

    [Test]
    public async Task ValidateCredentials_WhenLocalUserHasNoPassword_SkipsLockoutAndFallsBackToLdap()
    {
        var user = new AppUser { Id = "4", Email = Email };
        _userManager.FindByEmailAsync(Email).Returns(user);
        _userManager.HasPasswordAsync(user).Returns(false);

        var (isValid, resultUser) = await _sut.ValidateCredentialsAsync(Email, Password);

        isValid.ShouldBeFalse();
        resultUser.ShouldBeNull();
        await _userManager.DidNotReceive().IsLockedOutAsync(user);
        await _userManager.DidNotReceive().AccessFailedAsync(user);
        await _identityProviderRepository.Received(1).GetAuthenticationProviders();
    }

    private static UserManager<AppUser> CreateUserManager()
    {
        var userStore = Substitute.For<IUserStore<AppUser>>();
        var identityOptions = Substitute.For<IOptions<IdentityOptions>>();
        var passwordHasher = Substitute.For<IPasswordHasher<AppUser>>();
        var userValidators = new List<IUserValidator<AppUser>>();
        var passwordValidators = new List<IPasswordValidator<AppUser>>();
        var keyNormalizer = Substitute.For<ILookupNormalizer>();
        var errorDescriber = Substitute.For<IdentityErrorDescriber>();
        var serviceProvider = Substitute.For<IServiceProvider>();
        var logger = Substitute.For<ILogger<UserManager<AppUser>>>();

        return Substitute.For<UserManager<AppUser>>(
            userStore, identityOptions, passwordHasher, userValidators, passwordValidators,
            keyNormalizer, errorDescriber, serviceProvider, logger);
    }
}
