// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Builds the real region-setup object graph (RegionSetupService, RegionEntityImportService and every
/// collaborator, wired through the production AddRegionSetupServices registration) on top of the
/// substituted repositories a test supplies, so the region-setup tests exercise the same composition as
/// the application.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Interfaces.Settings;
using Klacks.Api.Domain.Events;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Interfaces.Scheduling;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Interfaces.Staffs;
using Klacks.Api.Infrastructure.Extensions;
using Klacks.Api.Infrastructure.Services.Settings;
using Klacks.Api.Infrastructure.Services.Settings.RegionSetup;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.TestHelpers;

internal static class RegionSetupTestServiceFactory
{
    public static RegionSetupService CreateRegionSetupService(
        IConfiguration configuration,
        ILanguagePluginService languagePluginService,
        ISettingsRepository settingsRepository,
        ICalendarSelectionRepository calendarSelectionRepository,
        IPeriodCapRuleRepository periodCapRuleRepository,
        IRestDayRotationRuleRepository restDayRotationRuleRepository,
        ICounterRuleRepository counterRuleRepository,
        IRestrictedTimeWindowRuleRepository restrictedTimeWindowRuleRepository,
        IGroupRepository groupRepository,
        ISchedulingRuleImportRepository schedulingRuleImportRepository,
        ISchedulingRuleRateRevisionImportRepository schedulingRuleRateRevisionImportRepository,
        IQualificationImportRepository qualificationImportRepository,
        IMacroImportRepository macroImportRepository,
        IMacroScriptValidator macroScriptValidator,
        IUnitOfWork unitOfWork,
        IDomainEventDispatcher eventDispatcher,
        ILogger<RestrictedTimeWindowRuleImporter>? restrictedTimeWindowLogger = null)
    {
        var provider = BuildProvider(
            configuration, languagePluginService, settingsRepository, calendarSelectionRepository, periodCapRuleRepository,
            restDayRotationRuleRepository, counterRuleRepository, restrictedTimeWindowRuleRepository, groupRepository,
            schedulingRuleImportRepository, schedulingRuleRateRevisionImportRepository, qualificationImportRepository,
            macroImportRepository, macroScriptValidator, unitOfWork, eventDispatcher, restrictedTimeWindowLogger);
        return (RegionSetupService)provider.GetRequiredService<IRegionSetupService>();
    }

    public static IRegionEntityImportService CreateRegionEntityImportService(
        IConfiguration configuration,
        ILanguagePluginService languagePluginService,
        ISettingsRepository settingsRepository,
        ICalendarSelectionRepository calendarSelectionRepository,
        IPeriodCapRuleRepository periodCapRuleRepository,
        IRestDayRotationRuleRepository restDayRotationRuleRepository,
        ICounterRuleRepository counterRuleRepository,
        IRestrictedTimeWindowRuleRepository restrictedTimeWindowRuleRepository,
        IGroupRepository groupRepository,
        ISchedulingRuleImportRepository schedulingRuleImportRepository,
        ISchedulingRuleRateRevisionImportRepository schedulingRuleRateRevisionImportRepository,
        IQualificationImportRepository qualificationImportRepository,
        IMacroImportRepository macroImportRepository,
        IMacroScriptValidator macroScriptValidator,
        IUnitOfWork unitOfWork,
        IDomainEventDispatcher eventDispatcher)
    {
        var provider = BuildProvider(
            configuration, languagePluginService, settingsRepository, calendarSelectionRepository, periodCapRuleRepository,
            restDayRotationRuleRepository, counterRuleRepository, restrictedTimeWindowRuleRepository, groupRepository,
            schedulingRuleImportRepository, schedulingRuleRateRevisionImportRepository, qualificationImportRepository,
            macroImportRepository, macroScriptValidator, unitOfWork, eventDispatcher, null);
        return provider.GetRequiredService<IRegionEntityImportService>();
    }

    private static ServiceProvider BuildProvider(
        IConfiguration configuration,
        ILanguagePluginService languagePluginService,
        ISettingsRepository settingsRepository,
        ICalendarSelectionRepository calendarSelectionRepository,
        IPeriodCapRuleRepository periodCapRuleRepository,
        IRestDayRotationRuleRepository restDayRotationRuleRepository,
        ICounterRuleRepository counterRuleRepository,
        IRestrictedTimeWindowRuleRepository restrictedTimeWindowRuleRepository,
        IGroupRepository groupRepository,
        ISchedulingRuleImportRepository schedulingRuleImportRepository,
        ISchedulingRuleRateRevisionImportRepository schedulingRuleRateRevisionImportRepository,
        IQualificationImportRepository qualificationImportRepository,
        IMacroImportRepository macroImportRepository,
        IMacroScriptValidator macroScriptValidator,
        IUnitOfWork unitOfWork,
        IDomainEventDispatcher eventDispatcher,
        ILogger<RestrictedTimeWindowRuleImporter>? restrictedTimeWindowLogger)
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddSingleton(configuration);
        services.AddSingleton(languagePluginService);
        services.AddSingleton(settingsRepository);
        services.AddSingleton(calendarSelectionRepository);
        services.AddSingleton(periodCapRuleRepository);
        services.AddSingleton(restDayRotationRuleRepository);
        services.AddSingleton(counterRuleRepository);
        services.AddSingleton(restrictedTimeWindowRuleRepository);
        services.AddSingleton(groupRepository);
        services.AddSingleton(schedulingRuleImportRepository);
        services.AddSingleton(schedulingRuleRateRevisionImportRepository);
        services.AddSingleton(qualificationImportRepository);
        services.AddSingleton(macroImportRepository);
        services.AddSingleton(macroScriptValidator);
        services.AddSingleton(unitOfWork);
        services.AddSingleton(eventDispatcher);
        services.AddRegionSetupServices();

        if (restrictedTimeWindowLogger != null)
        {
            services.AddSingleton(restrictedTimeWindowLogger);
        }

        return services.BuildServiceProvider(new ServiceProviderOptions { ValidateOnBuild = true });
    }
}
