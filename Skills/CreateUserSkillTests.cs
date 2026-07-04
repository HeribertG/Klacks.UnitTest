// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for create_user: the skill rejects duplicate emails up front, otherwise registers the
/// account inside a transaction that re-reads it via a no-tracking query and rolls back (surfacing an
/// error) when registration fails or the re-read does not confirm the write.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Accounts;
using Microsoft.AspNetCore.Identity;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CreateUserSkillTests
{
    private const string FirstName = "John";
    private const string LastName = "Doe";
    private const string Email = "john.doe@example.com";
    private const string Username = "jdoe";

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "Admin" }
    };

    private static IUnitOfWork UnitOfWorkThatExecutes()
    {
        var unitOfWork = Substitute.For<IUnitOfWork>();
        unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<string>>>())
            .Returns(call => call.Arg<Func<Task<string>>>()());
        return unitOfWork;
    }

    private static IUsernameGeneratorService UsernameGenerator()
    {
        var generator = Substitute.For<IUsernameGeneratorService>();
        generator.GenerateUniqueUsernameAsync(FirstName, LastName).Returns(Username);
        return generator;
    }

    [Test]
    public async Task ExplicitValues_CreatesUser_AndConfirmsPersistence()
    {
        var users = Substitute.For<IUserManagementService>();
        users.FindUserByEmailAsync(Email).Returns((AppUser?)null);
        users.RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>())
            .Returns((true, (IdentityResult?)null));
        users.FindUserByIdNoTrackingAsync(Arg.Any<string>())
            .Returns(call => new AppUser
            {
                Id = call.Arg<string>(),
                FirstName = FirstName,
                LastName = LastName,
                Email = Email,
                UserName = Username
            });

        var skill = new CreateUserSkill(users, UsernameGenerator(), UnitOfWorkThatExecutes());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["firstName"] = FirstName,
            ["lastName"] = LastName,
            ["email"] = Email
        });

        result.Success.ShouldBeTrue();
        result.Message.ShouldContain("confirmed in the database (verified)");
        await users.Received(1).RegisterUserAsync(
            Arg.Is<AppUser>(u => u.FirstName == FirstName && u.LastName == LastName && u.Email == Email && u.UserName == Username),
            Arg.Any<string>());
    }

    [Test]
    public async Task DuplicateEmail_ReturnsError_NoRegistration()
    {
        var users = Substitute.For<IUserManagementService>();
        users.FindUserByEmailAsync(Email).Returns(new AppUser { Email = Email });

        var skill = new CreateUserSkill(users, UsernameGenerator(), Substitute.For<IUnitOfWork>());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["firstName"] = FirstName,
            ["lastName"] = LastName,
            ["email"] = Email
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("already exists");
        await users.DidNotReceive().RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>());
    }

    [Test]
    public async Task RegistrationFailure_ReturnsErrorWithReason()
    {
        var users = Substitute.For<IUserManagementService>();
        users.FindUserByEmailAsync(Email).Returns((AppUser?)null);
        users.RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>())
            .Returns((false, IdentityResult.Failed(new IdentityError { Description = "Duplicate username" })));

        var skill = new CreateUserSkill(users, UsernameGenerator(), UnitOfWorkThatExecutes());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["firstName"] = FirstName,
            ["lastName"] = LastName,
            ["email"] = Email
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Duplicate username");
    }

    [Test]
    public async Task VerificationMismatch_ReturnsError_WhenReReadDoesNotConfirmTheWrite()
    {
        var users = Substitute.For<IUserManagementService>();
        users.FindUserByEmailAsync(Email).Returns((AppUser?)null);
        users.RegisterUserAsync(Arg.Any<AppUser>(), Arg.Any<string>())
            .Returns((true, (IdentityResult?)null));
        users.FindUserByIdNoTrackingAsync(Arg.Any<string>()).Returns((AppUser?)null);

        var skill = new CreateUserSkill(users, UsernameGenerator(), UnitOfWorkThatExecutes());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["firstName"] = FirstName,
            ["lastName"] = LastName,
            ["email"] = Email
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("could not be confirmed in the database");
    }
}
