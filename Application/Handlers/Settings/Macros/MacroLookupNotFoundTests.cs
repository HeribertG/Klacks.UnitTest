// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Pins down how a missing macro travels through the lookup chain, which the macro skills rely on: the management
/// service throws InvalidOperationException, the settings repository turns it into null, and the GetQuery handler
/// answers with null. No InvalidRequestException is thrown on the way, so a skill has nothing to catch around
/// GetQuery and only needs to handle the null result.
/// </summary>

using Klacks.Api.Application.Handlers.Settings.Macro;
using Klacks.Api.Application.Mappers;
using Klacks.Api.Application.Queries.Settings.Macros;
using Klacks.Api.Domain.Interfaces.Macros;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Infrastructure.Repositories.Settings;
using Microsoft.Extensions.Logging;
using MacroEntity = Klacks.Api.Domain.Models.Settings.Macro;

namespace Klacks.UnitTest.Application.Handlers.Settings.Macros;

[TestFixture]
public class MacroLookupNotFoundTests
{
    [Test]
    public async Task SettingsRepository_MissingMacro_ReturnsNull()
    {
        var management = Substitute.For<IMacroManagementService>();
        management.GetMacroAsync(Arg.Any<Guid>())
            .Returns<MacroEntity>(_ => throw new InvalidOperationException("Macro not found"));
        var repository = new SettingsRepository(
            null!,
            Substitute.For<ICalendarRuleFilterService>(),
            Substitute.For<ICalendarRuleSortingService>(),
            Substitute.For<ICalendarRulePaginationService>(),
            management);

        var macro = await repository.GetMacro(Guid.NewGuid());

        macro.ShouldBeNull();
    }

    [Test]
    public async Task GetQueryHandler_MissingMacro_ReturnsNull_WithoutThrowing()
    {
        var repository = Substitute.For<ISettingsRepository>();
        repository.GetMacro(Arg.Any<Guid>()).Returns((MacroEntity?)null);
        var handler = new GetQueryHandler(repository, new SettingsMapper(), Substitute.For<ILogger<GetQueryHandler>>());

        var result = await handler.Handle(new GetQuery(Guid.NewGuid()), CancellationToken.None);

        result.ShouldBeNull();
    }
}
