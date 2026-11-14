// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Klacks.Api.Application.Services.OAuth;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;
using Klacks.UnitTest.TestHelpers;

namespace Klacks.UnitTest.OAuth;

[TestFixture]
public class OAuthRedirectTrustPolicyTests
{
    private const string ClaudeCallback = "https://claude.ai/api/mcp/auth_callback";
    private const string EvilCallback = "https://evil.example.com/callback";
    private const string LoopbackCallback = "http://localhost:33418/callback";

    private static OAuthClient ClientWith(params string[] uris)
    {
        return new OAuthClient
        {
            ClientId = "client-1",
            RedirectUrisJson = JsonSerializer.Serialize(uris)
        };
    }

    [Test]
    public async Task GetSettingsAsync_Playground_ReadsTheSettingsLikeAnyOtherInstallation()
    {
        var policy = OAuthTrustTestPolicy.Create(playground: true);

        var settings = await policy.GetSettingsAsync();

        settings.Mode.ShouldBe(OAuthConnectionMode.Trusted);
        settings.AllowWrite.ShouldBeTrue();
    }

    [Test]
    public void AccessTokenLifetime_OutsideThePlayground_IsThirtyDays()
    {
        OAuthTrustTestPolicy.Create().AccessTokenLifetime.ShouldBe(TimeSpan.FromDays(OAuthConstants.AccessTokenExpiresInDays));
    }

    [Test]
    public void AccessTokenLifetime_OnThePlayground_IsTwentyFourHours()
    {
        OAuthTrustTestPolicy.Create(playground: true).AccessTokenLifetime
            .ShouldBe(TimeSpan.FromHours(OAuthConstants.PlaygroundAccessTokenExpiresInHours));
        OAuthTrustTestPolicy.Create(playground: true).AccessTokenLifetime.ShouldBeLessThanOrEqualTo(TimeSpan.FromHours(24));
    }

    [Test]
    public async Task GetSettingsAsync_ReadsTheFourKeysInOneCall()
    {
        var reader = Substitute.For<ISettingsReader>();
        reader.GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string> { [SettingKeys.OAuthAllowWrite] = "false" });
        var mode = Substitute.For<Klacks.Api.Domain.Interfaces.Playground.IPlaygroundMode>();
        var policy = new OAuthRedirectTrustPolicy(reader, mode);

        var settings = await policy.GetSettingsAsync();

        settings.AllowWrite.ShouldBeFalse();
        await reader.Received(1).GetSettingsByTypesAsync(
            Arg.Is<IEnumerable<string>>(keys => keys.OrderBy(k => k).SequenceEqual(new[]
            {
                SettingKeys.OAuthAllowLoopback,
                SettingKeys.OAuthAllowWrite,
                SettingKeys.OAuthConnectionsMode,
                SettingKeys.OAuthCustomRedirectTargets
            }.OrderBy(k => k))),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ResolveAsync_RegisteredAndListed_ResolvesWithEntryOfTheList()
    {
        var resolution = await OAuthTrustTestPolicy.Create().ResolveAsync(ClientWith(ClaudeCallback), ClaudeCallback);

        resolution.IsResolved.ShouldBeTrue();
        resolution.RedirectUri.ShouldBe(ClaudeCallback);
        resolution.Match!.DisplayName.ShouldBe("Claude");
        resolution.Settings.AllowWrite.ShouldBeTrue();
    }

    [Test]
    public async Task ResolveAsync_NoRedirectAndSingleRegisteredListed_UsesTheRegisteredOne()
    {
        var resolution = await OAuthTrustTestPolicy.Create().ResolveAsync(ClientWith(ClaudeCallback), null);

        resolution.IsResolved.ShouldBeTrue();
        resolution.RedirectUri.ShouldBe(ClaudeCallback);
    }

    [Test]
    public async Task ResolveAsync_NoRedirectAndSingleRegisteredUntrusted_IsRefused()
    {
        var resolution = await OAuthTrustTestPolicy.Create().ResolveAsync(ClientWith(EvilCallback), null);

        resolution.IsResolved.ShouldBeFalse();
        resolution.Error.ShouldBe(OAuthConstants.ErrorInvalidRequest);
        resolution.ErrorDescription.ShouldContain("https://evil.example.com");
    }

    [Test]
    public async Task ResolveAsync_NoRedirectAndSeveralRegistered_RequiresRedirectUri()
    {
        var resolution = await OAuthTrustTestPolicy.Create().ResolveAsync(ClientWith(ClaudeCallback, LoopbackCallback), "");

        resolution.IsResolved.ShouldBeFalse();
        resolution.Error.ShouldBe(OAuthConstants.ErrorInvalidRequest);
    }

    [Test]
    public async Task ResolveAsync_RedirectNotRegistered_IsRefusedEvenWhenListed()
    {
        var resolution = await OAuthTrustTestPolicy.Create().ResolveAsync(ClientWith(LoopbackCallback), ClaudeCallback);

        resolution.IsResolved.ShouldBeFalse();
        resolution.ErrorDescription.ShouldContain("not registered");
    }

    [Test]
    public async Task ResolveAsync_OldRegistrationWithForeignTarget_IsRefused()
    {
        var resolution = await OAuthTrustTestPolicy.Create().ResolveAsync(ClientWith(EvilCallback), EvilCallback);

        resolution.IsResolved.ShouldBeFalse();
        resolution.Error.ShouldBe(OAuthConstants.ErrorInvalidRequest);
        resolution.ErrorDescription.ShouldContain("not trusted");
        resolution.ErrorDescription.ShouldContain("administrator");
    }

    [Test]
    public async Task ResolveAsync_LoopbackWithSwitchOff_ReportsAccessDenied()
    {
        var policy = OAuthTrustTestPolicy.WithSetting(SettingKeys.OAuthAllowLoopback, "false");

        var resolution = await policy.ResolveAsync(ClientWith(LoopbackCallback), LoopbackCallback);

        resolution.IsResolved.ShouldBeFalse();
        resolution.Error.ShouldBe(OAuthConstants.ErrorAccessDenied);
        resolution.ErrorDescription.ShouldBe(OAuthTrustMessages.LocalProgramsDisabledDescription);
    }

    [Test]
    public async Task ResolveAsync_ModeOff_RefusesEvenListedVendors()
    {
        var policy = OAuthTrustTestPolicy.WithSetting(SettingKeys.OAuthConnectionsMode, "off");

        var resolution = await policy.ResolveAsync(ClientWith(ClaudeCallback), ClaudeCallback);

        resolution.IsResolved.ShouldBeFalse();
        resolution.Error.ShouldBe(OAuthConstants.ErrorAccessDenied);
        resolution.ErrorDescription.ShouldBe(OAuthTrustMessages.ConnectionsDisabledDescription);
    }

    [Test]
    public async Task ResolveAsync_Playground_ResolvesListedVendors()
    {
        var policy = OAuthTrustTestPolicy.Create(playground: true);

        var resolution = await policy.ResolveAsync(ClientWith(ClaudeCallback), ClaudeCallback);

        resolution.IsResolved.ShouldBeTrue();
    }

    [Test]
    public async Task ResolveAsync_CustomTargetOfTheAdministrator_ResolvesWithHostAsName()
    {
        var policy = OAuthTrustTestPolicy.WithCustomTargets("https://mcp.example.org/cb");

        var resolution = await policy.ResolveAsync(ClientWith("https://mcp.example.org/cb"), "https://mcp.example.org/cb");

        resolution.IsResolved.ShouldBeTrue();
        resolution.Match!.DisplayName.ShouldBe("mcp.example.org");
        resolution.Match.Class.ShouldBe(OAuthRedirectClass.Custom);
    }
}
