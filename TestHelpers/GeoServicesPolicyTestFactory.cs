// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Domain.Models.Settings.GeoServices;
using Klacks.Api.Domain.Services.GeoServices;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// Builds real GeoServicesPolicy instances for tests (a substituted policy would return null records).
/// Default() matches the code defaults (public services on), NoOutbound() switches every service without own URL off.
/// </summary>
public static class GeoServicesPolicyTestFactory
{
    public static GeoServicesPolicy Default() => With(_ => { });

    public static GeoServicesPolicy NoOutbound() => With(options => options.NoOutboundTraffic = true);

    public static GeoServicesPolicy With(Action<GeoServicesOptions> configure)
    {
        var options = new GeoServicesOptions();
        configure(options);
        return new GeoServicesPolicy(Options.Create(options));
    }
}
