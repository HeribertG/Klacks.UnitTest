// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Schedules;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Infrastructure.Persistence;
using Klacks.Api.Infrastructure.Services.AnalyseScenarios;
using Klacks.Api.Infrastructure.Services.Shifts;
using Microsoft.Extensions.DependencyInjection;

namespace Klacks.UnitTest.TestHelpers;

/// <summary>
/// Builds the real AnalyseScenarioService object graph (the entry point and every collaborator, wired through the
/// production AddAnalyseScenarioServices registration) on top of the test's own DataBaseContext, so every collaborator
/// stages on the context the test reads back, exactly as they share the request scope's context in the application.
/// The caller's rights (read by the variant accept rule) come from a substitute IUserService: Admin unless the test passes
/// its own rights, so a test that accepts a scenario with keys keeps working and a test that wants the refusal states it.
/// The group visibility of the variant accept rule is unrestricted.
/// </summary>
internal static class AnalyseScenarioTestServiceFactory
{
    public static AnalyseScenarioService Create(DataBaseContext context, IReadOnlyList<string>? rights = null)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(context);
        services.AddSingleton(CreateUserService(rights));
        services.AddSingleton(TestGroupWriteVisibility.UnrestrictedGroups());
        services.AddScoped<IShiftTreeService, ShiftTreeService>();
        services.AddAnalyseScenarioServices();
        return (AnalyseScenarioService)services.BuildServiceProvider().GetRequiredService<IAnalyseScenarioService>();
    }

    private static IUserService CreateUserService(IReadOnlyList<string>? rights)
    {
        var userService = Substitute.For<IUserService>();
        userService.GetRights().Returns(rights ?? Permissions.ExpandRoles([Roles.Admin]));
        return userService;
    }
}
