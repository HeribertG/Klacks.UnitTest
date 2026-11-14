// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Authentification;

namespace Klacks.UnitTest.OAuth;

[TestFixture]
public class OAuthTrustSettingsTests
{
    private const string ClaudeCallback = "https://claude.ai/api/mcp/auth_callback";
    private const string LoopbackCallback = "http://localhost:33418/callback";
    private const string CustomCallback = "https://mcp.example.org/cb";

    private static OAuthTrustSettings From(params (string Key, string Value)[] values)
    {
        return OAuthTrustSettings.FromValues(values.ToDictionary(v => v.Key, v => v.Value));
    }

    [Test]
    public void FromValues_NothingSet_TakesDefaults()
    {
        var settings = From();

        settings.Mode.ShouldBe(OAuthConnectionMode.Trusted);
        settings.AllowLoopback.ShouldBeTrue();
        settings.AllowWrite.ShouldBeTrue();
        settings.CustomTargets.ShouldBeEmpty();
    }

    [TestCase("")]
    [TestCase("   ")]
    public void FromValues_EmptySwitches_TakeDefaultOn(string value)
    {
        var settings = From((SettingKeys.OAuthAllowLoopback, value), (SettingKeys.OAuthAllowWrite, value));

        settings.AllowLoopback.ShouldBeTrue();
        settings.AllowWrite.ShouldBeTrue();
    }

    [TestCase("true", true)]
    [TestCase("True", true)]
    [TestCase(" true ", true)]
    [TestCase("false", false)]
    [TestCase("False", false)]
    public void FromValues_ParseableSwitch_IsHonoured(string value, bool expected)
    {
        var settings = From((SettingKeys.OAuthAllowLoopback, value), (SettingKeys.OAuthAllowWrite, value));

        settings.AllowLoopback.ShouldBe(expected);
        settings.AllowWrite.ShouldBe(expected);
    }

    [TestCase("yes")]
    [TestCase("1")]
    [TestCase("on")]
    [TestCase("garbage")]
    public void FromValues_UnparseableSwitch_FailsClosed(string value)
    {
        var settings = From((SettingKeys.OAuthAllowLoopback, value), (SettingKeys.OAuthAllowWrite, value));

        settings.AllowLoopback.ShouldBeFalse();
        settings.AllowWrite.ShouldBeFalse();
    }

    [TestCase("off", OAuthConnectionMode.Off)]
    [TestCase("OFF", OAuthConnectionMode.Off)]
    [TestCase(" off ", OAuthConnectionMode.Off)]
    [TestCase("trusted", OAuthConnectionMode.Trusted)]
    [TestCase("", OAuthConnectionMode.Trusted)]
    [TestCase("open", OAuthConnectionMode.Trusted)]
    [TestCase("all", OAuthConnectionMode.Trusted)]
    [TestCase("true", OAuthConnectionMode.Trusted)]
    [TestCase("garbage", OAuthConnectionMode.Trusted)]
    public void FromValues_Mode_NeverBecomesOpenForAll(string value, OAuthConnectionMode expected)
    {
        From((SettingKeys.OAuthConnectionsMode, value)).Mode.ShouldBe(expected);
    }

    [Test]
    public void FromValues_UnparseableCustomTargets_YieldNone()
    {
        From((SettingKeys.OAuthCustomRedirectTargets, "not json")).CustomTargets.ShouldBeEmpty();
    }

    [Test]
    public void FromValues_CustomTargets_AreCanonicalised()
    {
        var settings = From((SettingKeys.OAuthCustomRedirectTargets, "[\"https://MCP.example.org/cb\"]"));

        settings.CustomTargets.ShouldBe([CustomCallback]);
    }

    [Test]
    public void Evaluate_ListedVendor_IsTrusted()
    {
        var decision = From().Evaluate(ClaudeCallback);

        decision.IsTrusted.ShouldBeTrue();
        decision.Match!.DisplayName.ShouldBe("Claude");
        decision.Target.ShouldBeEmpty();
    }

    [Test]
    public void Evaluate_UnknownHttpsTarget_IsNotTrustedAndNamesTheTarget()
    {
        var decision = From().Evaluate("https://evil.example/cb");

        decision.Outcome.ShouldBe(OAuthTrustOutcome.NotTrusted);
        decision.Match.ShouldBeNull();
        decision.Target.ShouldBe("https://evil.example");
    }

    [Test]
    public void Evaluate_LoopbackWithSwitchOn_IsTrusted_WithSwitchOffReportsLocalProgramsDisabled()
    {
        From().Evaluate(LoopbackCallback).IsTrusted.ShouldBeTrue();

        var off = From((SettingKeys.OAuthAllowLoopback, "false")).Evaluate(LoopbackCallback);

        off.Outcome.ShouldBe(OAuthTrustOutcome.LocalProgramsDisabled);
        off.IsTrusted.ShouldBeFalse();
    }

    [Test]
    public void Evaluate_VsCodeWebRedirect_FollowsTheLocalProgramsSwitch()
    {
        From().Evaluate(OAuthTrustedRedirects.VsCodeWebRedirectUri).IsTrusted.ShouldBeTrue();

        From((SettingKeys.OAuthAllowLoopback, "false")).Evaluate(OAuthTrustedRedirects.VsCodeWebRedirectUri)
            .Outcome.ShouldBe(OAuthTrustOutcome.LocalProgramsDisabled);
    }

    [Test]
    public void Evaluate_UnparseableLoopbackSwitch_RefusesLocalPrograms()
    {
        From((SettingKeys.OAuthAllowLoopback, "garbage")).Evaluate(LoopbackCallback).IsTrusted.ShouldBeFalse();
    }

    [Test]
    public void Evaluate_ModeOff_RefusesEverythingIncludingListedVendors()
    {
        var settings = From((SettingKeys.OAuthConnectionsMode, "off"), (SettingKeys.OAuthCustomRedirectTargets, "[\"" + CustomCallback + "\"]"));

        foreach (var uri in new[] { ClaudeCallback, LoopbackCallback, CustomCallback })
        {
            settings.Evaluate(uri).Outcome.ShouldBe(OAuthTrustOutcome.ConnectionsDisabled);
        }
    }

    [Test]
    public void Evaluate_CustomTarget_IsTrustedOnlyOnceAdded()
    {
        From().Evaluate(CustomCallback).IsTrusted.ShouldBeFalse();

        var decision = From((SettingKeys.OAuthCustomRedirectTargets, "[\"" + CustomCallback + "\"]")).Evaluate(CustomCallback);

        decision.IsTrusted.ShouldBeTrue();
        decision.Match!.Class.ShouldBe(OAuthRedirectClass.Custom);
    }
}
