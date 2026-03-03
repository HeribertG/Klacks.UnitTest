using FluentValidation.Results;
using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.Validation.Accounts;
using Klacks.Api.Domain.Models.Authentification;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using System.Security.Claims;

namespace Klacks.UnitTest.Validation.Accounts;

[TestFixture]
public class ChangeRoleCommandValidatorTests
{
    private ChangeRoleCommandValidator _validator;
    private IHttpContextAccessor _httpContextAccessor;

    [SetUp]
    public void Setup()
    {
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _validator = new ChangeRoleCommandValidator(_httpContextAccessor);
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenUserIdIsEmpty()
    {
        var changeRole = new ChangeRole
        {
            UserId = string.Empty,
            RoleName = "Admin",
            IsSelected = true
        };
        var command = new ChangeRoleCommand(changeRole);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "User ID is required."));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenUserTriesToChangeOwnRole()
    {
        var currentUserId = Guid.NewGuid().ToString();
        var changeRole = new ChangeRole
        {
            UserId = currentUserId,
            RoleName = "Admin",
            IsSelected = true
        };
        var command = new ChangeRoleCommand(changeRole);

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, currentUserId)
        }));
        _httpContextAccessor.HttpContext.Returns(httpContext);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "You cannot change your own role."));
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenUserTriesToChangeDifferentUserRole()
    {
        var currentUserId = Guid.NewGuid().ToString();
        var targetUserId = Guid.NewGuid().ToString();
        var changeRole = new ChangeRole
        {
            UserId = targetUserId,
            RoleName = "Admin",
            IsSelected = true
        };
        var command = new ChangeRoleCommand(changeRole);

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, currentUserId)
        }));
        _httpContextAccessor.HttpContext.Returns(httpContext);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenHttpContextIsNull()
    {
        var targetUserId = Guid.NewGuid().ToString();
        var changeRole = new ChangeRole
        {
            UserId = targetUserId,
            RoleName = "Admin",
            IsSelected = true
        };
        var command = new ChangeRoleCommand(changeRole);

        _httpContextAccessor.HttpContext.Returns((HttpContext)null);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenUserClaimIsNull()
    {
        var targetUserId = Guid.NewGuid().ToString();
        var changeRole = new ChangeRole
        {
            UserId = targetUserId,
            RoleName = "Admin",
            IsSelected = true
        };
        var command = new ChangeRoleCommand(changeRole);

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        _httpContextAccessor.HttpContext.Returns(httpContext);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenChangingToUserRole()
    {
        var currentUserId = Guid.NewGuid().ToString();
        var targetUserId = Guid.NewGuid().ToString();
        var changeRole = new ChangeRole
        {
            UserId = targetUserId,
            RoleName = "User",
            IsSelected = false
        };
        var command = new ChangeRoleCommand(changeRole);

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, currentUserId)
        }));
        _httpContextAccessor.HttpContext.Returns(httpContext);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenChangingAuthorisedRole()
    {
        var currentUserId = Guid.NewGuid().ToString();
        var targetUserId = Guid.NewGuid().ToString();
        var changeRole = new ChangeRole
        {
            UserId = targetUserId,
            RoleName = "Authorised",
            IsSelected = true
        };
        var command = new ChangeRoleCommand(changeRole);

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, currentUserId)
        }));
        _httpContextAccessor.HttpContext.Returns(httpContext);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }
}
