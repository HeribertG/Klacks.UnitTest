
using Klacks.Api.Datas;
using Klacks.Api.Interfaces;
using Klacks.Api.Interfaces.Domains;
using Klacks.Api.Models.Authentification;
using Klacks.Api.Repositories;
using Klacks.Api.Resources;
using Klacks.Api.Resources.Registrations;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc.ModelBinding;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NUnit.Framework.Internal;
using NSubstitute;

namespace UnitTest.Repository;

[TestFixture]
public class AccountTests
{
    public IHttpContextAccessor _httpContextAccessor = null!;
    private readonly string Token = new RefreshTokenGenerator().GenerateRefreshToken();
    private AccountRepository _accountRepository = null!;
    private DataBaseContext _dbContext;
    private JwtSettings _jwtSettings = null!;
    private ITokenService? _tokenService = null!;
    private UserManager<AppUser> _userManager;
    private IAuthenticationService _authenticationService;
    private IUserManagementService _userManagementService;
    private IRefreshTokenService _refreshTokenService;

    [Test]
    public async Task ChangeRoleUser_ShouldChangeRole_WhenNewRoleIsValid()
    {
        // Arrange

        var changeRole = new ChangeRole
        {
            UserId = "672f77e8-e479-4422-8781-84d218377fb3",
            RoleName = "User",
            IsSelected = true
        };

        // Act
        var result = await _accountRepository.ChangeRoleUser(changeRole);

        // Assert
        Assert.That(result.Success, Is.True);
    }

    [Test]
    public async Task LogInUser_ShouldFailWhenUserNotFound()
    {
        // Arrange
        string email = "notfound@test.com";
        string password = "TestPassword123!";

        // Setup mock to return no user for this email
        _authenticationService.ValidateCredentialsAsync(email, password)
            .Returns(Task.FromResult((false, (AppUser)null)));

        // Act
        var result = await _accountRepository.LogInUser(email, password);

        // Assert
        Assert.That(result.Success, Is.False, "Login should fail when user is not found.");
        Assert.That(result.ModelState, Contains.Key("Login failed"));
    }

    [Test]
    public async Task LogInUser_ShouldFailWithWrongPassword()
    {
        // Arrange
        var user = new AppUser
        {
            UserName = "MyUser",
            Email = "admin@test.com"
        };
        string wrongPassword = "WrongPassword!";

        // Setup mock to return validation failure
        _authenticationService.ValidateCredentialsAsync(user.Email, wrongPassword)
            .Returns(Task.FromResult((false, (AppUser)null)));

        // Act
        var result = await _accountRepository.LogInUser(user.Email, wrongPassword);

        // Assert
        Assert.That(result.Success, Is.False, "Login should fail with the wrong password.");
        Assert.That(result.ModelState, Contains.Key("Login failed"));
    }

    [Test]
    public async Task LogInUser_ShouldLogInSuccessfully()
    {
        // Arrange
        var user = new AppUser
        {
            Id = "test-user-id",
            UserName = "MyUser",
            Email = "admin@test.com",
            FirstName = "Test",
            LastName = "User"
        };
        string _email = "admin@test.com";
        string password = "P@ssw0rt1";

        // Setup mocks for successful login
        _authenticationService.ValidateCredentialsAsync(_email, password)
            .Returns(Task.FromResult((true, user)));
            
        _userManagementService.IsUserInRoleAsync(user, "Admin")
            .Returns(Task.FromResult(false));
            
        _userManagementService.IsUserInRoleAsync(user, "Authorised")
            .Returns(Task.FromResult(false));
            
        _refreshTokenService.CreateRefreshTokenAsync(user.Id)
            .Returns(Task.FromResult("test-refresh-token"));
            
        _tokenService.CreateToken(user, Arg.Any<DateTime>())
            .Returns(Task.FromResult("test-jwt-token"));

        // Act
        var result = await _accountRepository.LogInUser(_email, password);

        // Assert
        Assert.That(result.Success, Is.True, "User should be logged in successfully.");
        Assert.That(result.Token, Is.EqualTo("test-jwt-token"));
        Assert.That(result.RefreshToken, Is.EqualTo("test-refresh-token"));
        Assert.That(result.UserName, Is.EqualTo(user.UserName));
    }

    [Test]
    public async Task RegisterUser_ShouldRegisterSuccessfully()
    {
        // Arrange
        // Use the already initialized repository from SetUp
        var accountRepository = _accountRepository;
        var user = new AppUser
        {
            UserName = "MyUser",
            FirstName = "Test",
            LastName = "Test",
            Email = "123@test.com"
        };
        string password = "TestPassword123!";

        // Act
        var result = await accountRepository.RegisterUser(user, password);

        // Assert
        Assert.That(result.Success, Is.True, "User should be registered successfully.");
    }

    [SetUp]
    public void Setup()
    {
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _tokenService = Substitute.For<ITokenService>();
        _authenticationService = Substitute.For<IAuthenticationService>();
        _userManagementService = Substitute.For<IUserManagementService>();
        _refreshTokenService = Substitute.For<IRefreshTokenService>();
        
        var options = new DbContextOptionsBuilder<DataBaseContext>()
            .UseInMemoryDatabase(databaseName: Guid.NewGuid().ToString()).Options;

        _dbContext = new DataBaseContext(options, _httpContextAccessor);
        
        // Setup mocks
        SetupMocks();

        _tokenService.CreateToken(Arg.Any<AppUser>(), Arg.Any<DateTime>()).Returns(Task.FromResult("1234567890"));

        _dbContext.Database.EnsureCreated();

        // Seed the database with test data
        SeedDatabase();

        // JwtSettings no longer needed with domain services

        _accountRepository = new AccountRepository(_dbContext, _tokenService, _authenticationService, _userManagementService, _refreshTokenService);
    }

    [TearDown]
    public void Teardown()
    {
        if (_dbContext != null)
        {
            _dbContext.Database.EnsureDeleted();
            _dbContext.Dispose();
        }
    }

    [Test]
    public async Task ValidateRefreshTokenAsync_ShouldReturnTrue()
    {
        // Arrange
        var user = await _dbContext.Users.FirstAsync() as AppUser;
        var validToken = Token;

        // Act
        var result = await _accountRepository.ValidateRefreshTokenAsync(user!, validToken);

        // Assert
        Assert.That(result, Is.True);
    }



    private void SeedDatabase()
    {
        var roles = new[]
        {
        new IdentityRole { Id = "9c05bb10-5855-4201-a755-1d92ed9df000", ConcurrencyStamp = "d94790da-0103-4ade-b715-29526b2b1fc7", Name = "Authorised", NormalizedName = "AUTHORISED" },
        new IdentityRole { Id = "e32d7319-6861-4c9a-b096-08a77088cadd", ConcurrencyStamp = "402b8312-92a7-43f4-be73-b3400ccc2a7b", Name = "Admin", NormalizedName = "ADMIN" }
    };

        _dbContext.Roles.AddRange(roles);

        var user = new AppUser
        {
            Id = "672f77e8-e479-4422-8781-84d218377fb3",
            AccessFailedCount = 0,
            ConcurrencyStamp = "217b0216-5440-4e51-a6e4-ea79d0da9155",
            Email = "admin@test.com",
            EmailConfirmed = true,
            FirstName = "admin",
            LastName = "admin",
            LockoutEnabled = false,
            NormalizedEmail = "ADMIN@TEST.COM",
            NormalizedUserName = "ADMIN",
            PasswordHash = "AQAAAAEAACcQAAAAEM4rFqzwCkNDdqC7P5XDITL1ub4TLm1MPZMru7BlKyFLNSRfaamO4BUl/fAV4aNNlA==",
            PhoneNumber = "123456789",
            PhoneNumberConfirmed = false,
            SecurityStamp = "a04e4667-082e-43df-b82a-3ff914fc7db7",
            TwoFactorEnabled = false,
            UserName = "admin"
        };

        _dbContext.Users.Add(user);

        var userRoles = new[]
        {
        new IdentityUserRole<string> { RoleId = "9c05bb10-5855-4201-a755-1d92ed9df000", UserId = "672f77e8-e479-4422-8781-84d218377fb3" },
        new IdentityUserRole<string> { RoleId = "e32d7319-6861-4c9a-b096-08a77088cadd", UserId = "672f77e8-e479-4422-8781-84d218377fb3" }
    };

        _dbContext.UserRoles.AddRange(userRoles);

        var refreshToken = new RefreshToken
        {
            AspNetUsersId = user.Id,
            Token = Token,
            ExpiryDate = DateTime.UtcNow.AddHours(1),
        };

        _dbContext.RefreshToken.Add(refreshToken);

        _dbContext.SaveChanges();
    }

    private void SetupMocks()
    {
        // Setup default authentication service mocks - specific test cases will override these
        _authenticationService.ValidateCredentialsAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(Task.FromResult((false, (AppUser)null)));

        _authenticationService.GetUserFromAccessTokenAsync(Arg.Any<string>())
            .Returns(Task.FromResult<AppUser>(null));

        _authenticationService.ChangePasswordAsync(Arg.Any<AppUser>(), Arg.Any<string>(), Arg.Any<string>())
            .Returns(callInfo => Task.FromResult((true, (IdentityResult?)null)));
            
        _authenticationService.AddErrorsToModelState(Arg.Any<IdentityResult>(), Arg.Any<ModelStateDictionary>())
            .Returns(callInfo => new ModelStateDictionary());
            
        // SetModelError is a void method, so we just need to set it up without Returns
        _authenticationService
            .When(x => x.SetModelError(Arg.Any<AuthenticatedResult>(), Arg.Any<string>(), Arg.Any<string>()))
            .Do(callInfo => 
            {
                var result = callInfo.ArgAt<AuthenticatedResult>(0);
                var key = callInfo.ArgAt<string>(1);
                var message = callInfo.ArgAt<string>(2);
                
                if (result.ModelState == null)
                    result.ModelState = new ModelStateDictionary();
                    
                result.ModelState.AddModelError(key, message);
            });

        // Setup user management service mocks
        _userManagementService.FindUserByEmailAsync(Arg.Any<string>())
            .Returns(callInfo => {
                var email = callInfo.ArgAt<string>(0);
                if (email == "admin@test.com")
                    return Task.FromResult(new AppUser { Email = email, UserName = "admin" });
                return Task.FromResult<AppUser>(null);
            });

        _userManagementService.ChangeUserRoleAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<bool>())
            .Returns(Task.FromResult((true, "Role changed successfully")));

        _userManagementService.DeleteUserAsync(Arg.Any<Guid>())
            .Returns(Task.FromResult((true, "User deleted successfully")));

        _userManagementService.GetUserListAsync()
            .Returns(callInfo => Task.FromResult(new List<UserResource>()));

        _userManagementService.RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>())
            .Returns(callInfo => Task.FromResult((true, IdentityResult.Success)));

        _userManagementService.IsUserInRoleAsync(Arg.Any<AppUser>(), "Admin")
            .Returns(callInfo => {
                var user = callInfo.ArgAt<AppUser>(0);
                return Task.FromResult(user?.UserName == "Admin");
            });

        _userManagementService.IsUserInRoleAsync(Arg.Any<AppUser>(), "Authorised")
            .Returns(callInfo => {
                var user = callInfo.ArgAt<AppUser>(0);
                return Task.FromResult(user?.UserName == "Authorised");
            });

        // Setup refresh token service mocks
        _refreshTokenService.CreateRefreshTokenAsync(Arg.Any<string>())
            .Returns(Task.FromResult(Token));

        _refreshTokenService.ValidateRefreshTokenAsync(Arg.Any<string>(), Arg.Any<string>())
            .Returns(callInfo => {
                var refreshToken = callInfo.ArgAt<string>(1);
                return Task.FromResult(refreshToken == Token);
            });

        _refreshTokenService.GetUserFromRefreshTokenAsync(Arg.Any<string>())
            .Returns(Task.FromResult<AppUser>(null));

        _refreshTokenService.CalculateTokenExpiryTime()
            .Returns(DateTime.UtcNow.AddHours(1));
    }
}
