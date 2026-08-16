// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the GDPR erasure part of UserManagementService.DeleteUserAsync — verifies that a
/// user deletion also clears personal data outside the core DbContext (the messaging plugin's
/// messenger identities above all), that this happens before the Identity row disappears, that the
/// deletion still succeeds when no plugin contributes a participant, and that a participant which
/// fails is reported instead of silently skipped.
/// </summary>

using Klacks.Api.Application.Services.Authentication;
using Klacks.Api.Domain.Interfaces.Authentification;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.UnitTest.TestHelpers;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Services.Authentication;

[TestFixture]
public class UserManagementServiceDeleteUserTests
{
    private const string MessengerContactsDataSet = "messaging plugin user messenger contacts";

    private UserManager<AppUser> _userManager = null!;
    private IUserDataEraser _userDataEraser = null!;
    private RecordingLogger<UserManagementService> _logger = null!;
    private Guid _userId;
    private AppUser _user = null!;

    [SetUp]
    public void Setup()
    {
        _userId = Guid.NewGuid();
        _user = new AppUser { Id = _userId.ToString(), UserName = "planner", Email = "planner@example.com" };
        _userManager = CreateUserManager();
        _userManager.FindByIdAsync(_userId.ToString()).Returns(_user);
        _userManager.DeleteAsync(_user).Returns(IdentityResult.Success);
        _userDataEraser = Substitute.For<IUserDataEraser>();
        _logger = new RecordingLogger<UserManagementService>();
    }

    [TearDown]
    public void TearDown()
    {
        _userManager.Dispose();
    }

    private UserManagementService Create(params IUserDataErasureParticipant[] participants) =>
        new(_userManager, _userDataEraser, participants, _logger);

    private static IUserDataErasureParticipant Participant(string name, int removed = 1)
    {
        var participant = Substitute.For<IUserDataErasureParticipant>();
        participant.DataSetName.Returns(name);
        participant.EraseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>()).Returns(removed);
        return participant;
    }

    private static IUserDataErasureParticipant BrokenParticipant(string name, Exception failure)
    {
        var participant = Substitute.For<IUserDataErasureParticipant>();
        participant.DataSetName.Returns(name);
        participant.EraseAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns<int>(_ => throw failure);
        return participant;
    }

    [Test]
    public async Task DeleteUserAsync_ErasesTheMessengerContactsOfThatUser()
    {
        var contacts = Participant(MessengerContactsDataSet);

        var (success, _) = await Create(contacts).DeleteUserAsync(_userId);

        Assert.That(success, Is.True);
        await contacts.Received(1).EraseAsync(_userId.ToString(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteUserAsync_ErasesEveryRegisteredParticipant()
    {
        var contacts = Participant(MessengerContactsDataSet);
        var other = Participant("another plugin data set");

        await Create(contacts, other).DeleteUserAsync(_userId);

        await contacts.Received(1).EraseAsync(_userId.ToString(), Arg.Any<CancellationToken>());
        await other.Received(1).EraseAsync(_userId.ToString(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteUserAsync_ErasesPluginDataBeforeTheIdentityRowDisappears()
    {
        var contacts = Participant(MessengerContactsDataSet);

        await Create(contacts).DeleteUserAsync(_userId);

        Received.InOrder(() =>
        {
            contacts.EraseAsync(_userId.ToString(), Arg.Any<CancellationToken>());
            _userManager.DeleteAsync(_user);
        });
    }

    [Test]
    public async Task DeleteUserAsync_WithoutAnyParticipant_StillDeletesTheUser()
    {
        var (success, message) = await Create().DeleteUserAsync(_userId);

        Assert.That(success, Is.True, message);
        await _userDataEraser.Received(1).EraseUserDataAsync(_userId, Arg.Any<CancellationToken>());
        await _userManager.Received(1).DeleteAsync(_user);
    }

    [Test]
    public async Task DeleteUserAsync_ParticipantThrows_StillDeletesTheUserAndTheOtherDataSets()
    {
        var broken = BrokenParticipant(
            MessengerContactsDataSet,
            new InvalidOperationException("Cannot create a DbSet for 'UserMessengerContact'"));
        var healthy = Participant("another plugin data set");

        var (success, message) = await Create(broken, healthy).DeleteUserAsync(_userId);

        Assert.That(success, Is.True, message);
        await healthy.Received(1).EraseAsync(_userId.ToString(), Arg.Any<CancellationToken>());
        await _userManager.Received(1).DeleteAsync(_user);
    }

    [Test]
    public async Task DeleteUserAsync_ParticipantThrows_IsLoggedAsWarningNamingTheDataSet()
    {
        var broken = BrokenParticipant(
            MessengerContactsDataSet,
            new InvalidOperationException("the entity is not part of the model"));

        await Create(broken).DeleteUserAsync(_userId);

        var warnings = _logger.Entries.Where(e => e.Level == LogLevel.Warning).ToList();
        Assert.That(warnings, Is.Not.Empty,
            "An erasure that silently skipped a data set is exactly what must not go unnoticed.");
        Assert.That(warnings.Any(e => e.Message.Contains(MessengerContactsDataSet, StringComparison.Ordinal)), Is.True,
            "The log entry must name the data set whose rows may still exist.");
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
