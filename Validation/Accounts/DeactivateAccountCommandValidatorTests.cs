// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using FluentValidation.Results;
using Klacks.Api.Application.Commands.Accounts;
using Klacks.Api.Application.Validation.Accounts;
using Microsoft.AspNetCore.Http;
using NSubstitute;
using System.Security.Claims;

namespace Klacks.UnitTest.Validation.Accounts;

[TestFixture]
public class DeactivateAccountCommandValidatorTests
{
    private const string SelfDeactivationMessage = "You cannot deactivate your own account.";

    private DeactivateAccountCommandValidator _validator = null!;
    private IHttpContextAccessor _httpContextAccessor = null!;

    [SetUp]
    public void Setup()
    {
        _httpContextAccessor = Substitute.For<IHttpContextAccessor>();
        _validator = new DeactivateAccountCommandValidator(_httpContextAccessor);
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenUserIdIsEmpty()
    {
        var command = new DeactivateAccountCommand(Guid.Empty);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == "User ID is required."));
    }

    [Test]
    public async Task Validate_ShouldBeInvalid_WhenAdminTriesToDeactivateOwnAccount()
    {
        var currentUserId = Guid.NewGuid();
        var command = new DeactivateAccountCommand(currentUserId);
        _httpContextAccessor.HttpContext.Returns(ContextFor(currentUserId));

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.False);
        Assert.That(result.Errors, Has.Some.Matches<ValidationFailure>(f => f.ErrorMessage == SelfDeactivationMessage));
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenTargetIsADifferentAccount()
    {
        var command = new DeactivateAccountCommand(Guid.NewGuid());
        _httpContextAccessor.HttpContext.Returns(ContextFor(Guid.NewGuid()));

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    [Test]
    public async Task Validate_ShouldBeValid_WhenHttpContextIsNull()
    {
        var command = new DeactivateAccountCommand(Guid.NewGuid());
        _httpContextAccessor.HttpContext.Returns((HttpContext?)null);

        var result = await _validator.ValidateAsync(command);

        Assert.That(result.IsValid, Is.True);
        Assert.That(result.Errors, Is.Empty);
    }

    private static DefaultHttpContext ContextFor(Guid userId)
    {
        var httpContext = new DefaultHttpContext();
        httpContext.User = new ClaimsPrincipal(new ClaimsIdentity(new[]
        {
            new Claim(ClaimTypes.NameIdentifier, userId.ToString())
        }));

        return httpContext;
    }
}
