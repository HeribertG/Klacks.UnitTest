// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The generated user name must always pass Identity's default AllowedUserNameCharacters, whatever script the name is
/// written in: the own-admin setup has no user name field any more, so a name Identity refuses would leave the
/// operator stuck with no way to fix it and the seeded admin still active.
/// </summary>

using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Domain.Services.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Services.Accounts;

[TestFixture]
public class UsernameGeneratorServiceTests
{
    private const int MinimumLength = 3;

    private UserManager<AppUser> _userManager = null!;
    private UsernameGeneratorService _sut = null!;

    [SetUp]
    public void SetUp()
    {
        _userManager = Substitute.For<UserManager<AppUser>>(
            Substitute.For<IUserStore<AppUser>>(),
            Substitute.For<IOptions<IdentityOptions>>(),
            Substitute.For<IPasswordHasher<AppUser>>(),
            new List<IUserValidator<AppUser>>(),
            new List<IPasswordValidator<AppUser>>(),
            Substitute.For<ILookupNormalizer>(),
            new IdentityErrorDescriber(),
            Substitute.For<IServiceProvider>(),
            Substitute.For<ILogger<UserManager<AppUser>>>());
        _userManager.FindByNameAsync(Arg.Any<string>()).Returns((AppUser?)null);

        _sut = new UsernameGeneratorService(_userManager, Substitute.For<ILogger<UsernameGeneratorService>>());
    }

    [TearDown]
    public void TearDown() => _userManager.Dispose();

    [TestCase("Max", "Mustermann", "mmu")]
    [TestCase("Jürgen", "Müller", "jmu")]
    [TestCase("José", "Ñúñez", "jnu")]
    [TestCase("Søren", "Łukasz", "slu")]
    [TestCase("Иван", "Петров", "usr")]
    [TestCase("田中", "太郎", "usr")]
    [TestCase("Ζωή", "Παππά", "usr")]
    [TestCase("محمد", "علي", "usr")]
    [TestCase("דוד", "כהן", "usr")]
    public async Task GenerateUniqueUsernameAsync_AnyScript_YieldsAnIdentityValidAsciiName(
        string firstName, string lastName, string expected)
    {
        var username = await _sut.GenerateUniqueUsernameAsync(firstName, lastName);

        username.ShouldBe(expected);
        username.Length.ShouldBeGreaterThanOrEqualTo(MinimumLength);
        username.ShouldAllBe(character => new IdentityOptions().User.AllowedUserNameCharacters.Contains(character));
    }

    [Test]
    public async Task GenerateUniqueUsernameAsync_TakenName_AppendsACounter()
    {
        _userManager.FindByNameAsync("mmu").Returns(new AppUser { UserName = "mmu" });

        var username = await _sut.GenerateUniqueUsernameAsync("Max", "Mustermann");

        username.ShouldBe("mmu1");
    }
}
