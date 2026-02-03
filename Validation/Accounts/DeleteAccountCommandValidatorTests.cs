using FluentValidation.Results;
using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.Validation.Accounts;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using System.Security.Claims;

namespace Klacks.UnitTest.Validation.Accounts;

[TestFixture]
public class DeleteAccountCommandValidatorTests
{
    private DeleteAccountCommandValidator _validator;
    private IHttpContextAccessor _httpContextAccessor;

    [SetUp]
    public void Setup()
    {
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _validator = new DeleteAccountCommandValidator(_httpContextAccessor);
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenUserIdIsEmpty()
    {
        var command = new DeleteAccountCommand(Guid.Empty);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "User ID is required."));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenUserTriesToDeleteOwnAccount()
    {
        var currentUserId = Guid.NewGuid();
        var command = new DeleteAccountCommand(currentUserId);

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, currentUserId.ToString())
        }));
        _httpContextAccessor.HttpContext.Returns(httpContext);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "You cannot delete your own account."));
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenUserTriesToDeleteDifferentAccount()
    {
        var currentUserId = Guid.NewGuid();
        var targetUserId = Guid.NewGuid();
        var command = new DeleteAccountCommand(targetUserId);

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, currentUserId.ToString())
        }));
        _httpContextAccessor.HttpContext.Returns(httpContext);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenHttpContextIsNull()
    {
        var targetUserId = Guid.NewGuid();
        var command = new DeleteAccountCommand(targetUserId);

        _httpContextAccessor.HttpContext.Returns((HttpContext)null);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenUserClaimIsNull()
    {
        var targetUserId = Guid.NewGuid();
        var command = new DeleteAccountCommand(targetUserId);

        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity());
        _httpContextAccessor.HttpContext.Returns(httpContext);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }
}
