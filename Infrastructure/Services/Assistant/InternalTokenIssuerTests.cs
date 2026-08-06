// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for InternalTokenIssuer, the component that lets background work act under a real
/// identity. They pin the security guarantees: roles are read fresh on every mint (so revoking one
/// takes effect immediately), the ceiling only ever removes rights, and a missing, locked-out or
/// role-less owner gets no token at all.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.Api.Infrastructure.Services.Assistant;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Logging.Abstractions;

namespace Klacks.UnitTest.Infrastructure.Services.Assistant;

[TestFixture]
public class InternalTokenIssuerTests
{
    private static readonly Guid OwnerId = Guid.NewGuid();

    private UserManager<AppUser> _userManager = null!;
    private ITokenService _tokenService = null!;
    private InternalTokenIssuer _issuer = null!;
    private AppUser _owner = null!;

    [SetUp]
    public void SetUp()
    {
        _userManager = Substitute.For<UserManager<AppUser>>(
            Substitute.For<IUserStore<AppUser>>(), null, null, null, null, null, null, null, null);
        _tokenService = Substitute.For<ITokenService>();
        _owner = new AppUser { Id = OwnerId.ToString(), UserName = "owner", FirstName = "O", LastName = "Wner" };

        _userManager.FindByIdAsync(OwnerId.ToString()).Returns(_owner);
        _userManager.IsLockedOutAsync(_owner).Returns(false);
        _userManager.GetRolesAsync(_owner).Returns(new List<string> { Roles.Authorised });
        _tokenService.CreateToken(
                Arg.Any<AppUser>(), Arg.Any<DateTime>(),
                Arg.Any<IReadOnlyList<string>?>(), Arg.Any<IReadOnlyDictionary<string, string>?>())
            .Returns("minted-jwt");

        _issuer = new InternalTokenIssuer(_userManager, _tokenService, NullLogger<InternalTokenIssuer>.Instance);
    }

    [TearDown]
    public void TearDown() => _userManager.Dispose();

    [Test]
    public async Task Issue_KnownOwner_MintsAShortLivedToken()
    {
        var result = await _issuer.IssueForOwnerAsync(OwnerId);

        result.Success.ShouldBeTrue();
        result.Token!.Value.ShouldBe("minted-jwt");

        await _tokenService.Received(1).CreateToken(
            _owner,
            Arg.Is<DateTime>(e => e <= DateTime.UtcNow.Add(InternalTokenIssuer.Lifetime).AddSeconds(5)
                                  && e > DateTime.UtcNow),
            Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>());
    }

    [Test]
    public async Task Issue_MarksTheTokenAsInternallyMinted()
    {
        await _issuer.IssueForOwnerAsync(OwnerId);

        await _tokenService.Received(1).CreateToken(
            Arg.Any<AppUser>(), Arg.Any<DateTime>(), Arg.Any<IReadOnlyList<string>?>(),
            Arg.Is<IReadOnlyDictionary<string, string>?>(c => c != null && c["token_use"] == "internal"));
    }

    [Test]
    public async Task Issue_UsesTheOwnersCurrentRoles_NotAFrozenSet()
    {
        _userManager.GetRolesAsync(_owner).Returns(new List<string> { Roles.Admin });

        await _issuer.IssueForOwnerAsync(OwnerId);

        await _tokenService.Received(1).CreateToken(
            Arg.Any<AppUser>(), Arg.Any<DateTime>(),
            Arg.Is<IReadOnlyList<string>?>(r => r != null && r.Contains(Roles.Admin)),
            Arg.Any<IReadOnlyDictionary<string, string>?>());
    }

    [Test]
    public async Task Issue_WithCeiling_DowngradesAdmin()
    {
        _userManager.GetRolesAsync(_owner).Returns(new List<string> { Roles.Admin });

        await _issuer.IssueForOwnerAsync(OwnerId, capToRole: Roles.Authorised);

        await _tokenService.Received(1).CreateToken(
            Arg.Any<AppUser>(), Arg.Any<DateTime>(),
            Arg.Is<IReadOnlyList<string>?>(r => r != null && !r.Contains(Roles.Admin) && r.Contains(Roles.Authorised)),
            Arg.Any<IReadOnlyDictionary<string, string>?>());
    }

    [Test]
    public async Task Issue_WithCeiling_NeverRaisesALesserRole()
    {
        _userManager.GetRolesAsync(_owner).Returns(new List<string> { Roles.User });

        await _issuer.IssueForOwnerAsync(OwnerId, capToRole: Roles.Authorised);

        await _tokenService.Received(1).CreateToken(
            Arg.Any<AppUser>(), Arg.Any<DateTime>(),
            Arg.Is<IReadOnlyList<string>?>(r => r != null && !r.Contains(Roles.Authorised) && r.Contains(Roles.User)),
            Arg.Any<IReadOnlyDictionary<string, string>?>());
    }

    [Test]
    public async Task Issue_UnknownOwner_IsRefused()
    {
        _userManager.FindByIdAsync(OwnerId.ToString()).Returns((AppUser?)null);

        var result = await _issuer.IssueForOwnerAsync(OwnerId);

        result.Success.ShouldBeFalse();
        result.Reason.ShouldContain("no longer exists");
        await _tokenService.DidNotReceive().CreateToken(
            Arg.Any<AppUser>(), Arg.Any<DateTime>(), Arg.Any<IReadOnlyList<string>?>(),
            Arg.Any<IReadOnlyDictionary<string, string>?>());
    }

    [Test]
    public async Task Issue_LockedOutOwner_IsRefused()
    {
        _userManager.IsLockedOutAsync(_owner).Returns(true);

        var result = await _issuer.IssueForOwnerAsync(OwnerId);

        result.Success.ShouldBeFalse();
        result.Reason.ShouldContain("locked out");
    }

    [Test]
    public async Task Issue_OwnerWithoutAnyRole_IsRefused()
    {
        _userManager.GetRolesAsync(_owner).Returns(new List<string>());

        var result = await _issuer.IssueForOwnerAsync(OwnerId);

        result.Success.ShouldBeFalse();
        result.Reason.ShouldContain("no role");
    }
}
