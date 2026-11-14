// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Builds the real OAuth redirect trust policy over a stubbed settings reader, so handler tests exercise the actual
/// matcher and settings parsing instead of a stub that would hide the rule under test.
/// </summary>
/// <param name="values">Setting rows by key; keys left out are absent and take their defaults</param>
/// <param name="playground">Whether the instance counts as the public Playground</param>

using Klacks.Api.Application.Interfaces.OAuth;
using Klacks.Api.Application.Services.OAuth;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Playground;

namespace Klacks.UnitTest.TestHelpers;

public static class OAuthTrustTestPolicy
{
    public static IOAuthRedirectTrustPolicy Create(IReadOnlyDictionary<string, string>? values = null, bool playground = false)
    {
        var settingsReader = Substitute.For<ISettingsReader>();
        settingsReader.GetSettingsByTypesAsync(Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>())
            .Returns(values ?? new Dictionary<string, string>());

        var mode = Substitute.For<IPlaygroundMode>();
        mode.IsActive.Returns(playground);

        return new OAuthRedirectTrustPolicy(settingsReader, mode);
    }

    public static IOAuthRedirectTrustPolicy WithSetting(string key, string value)
    {
        return Create(new Dictionary<string, string> { [key] = value });
    }

    public static IOAuthRedirectTrustPolicy WithCustomTargets(params string[] targets)
    {
        return WithSetting(SettingKeys.OAuthCustomRedirectTargets, System.Text.Json.JsonSerializer.Serialize(targets));
    }
}
