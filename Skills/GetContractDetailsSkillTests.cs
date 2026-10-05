// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using System.Text.Json;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.DTOs.Schedules;
using Klacks.Api.Application.Interfaces.Schedules;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class GetContractDetailsSkillTests
{
    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { "CanViewContracts" }
    };

    [Test]
    public async Task ExistingContract_ReturnsDetails()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(new ContractResource
            {
                Id = contractId,
                Name = "Vollzeit 160",
                GuaranteedHours = 160m,
                MinimumHours = 140m,
                MaximumHours = 180m,
                ValidFrom = new DateTime(2026, 1, 1)
            });
        var skill = new GetContractDetailsSkill(mediator, SourceResolver());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString()
        });

        result.Success.ShouldBeTrue();
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("Id").GetGuid().ShouldBe(contractId);
        data.GetProperty("Name").GetString().ShouldBe("Vollzeit 160");
        data.GetProperty("GuaranteedHours").GetDecimal().ShouldBe(160m);
        await mediator.Received(1).Send(
            Arg.Is<GetQuery<ContractResource>>(q => q.Id == contractId),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task UnknownContract_ReturnsError()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns<ContractResource>(_ => throw new KeyNotFoundException());
        var skill = new GetContractDetailsSkill(mediator, SourceResolver());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldNotBeNull();
        result.Message.ShouldContain("not found");
    }

    [Test]
    public async Task StandardRatesAndShiftWork_AreReportedAsNull_WithTheStandardMeaning()
    {
        var contractId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(new ContractResource
            {
                Id = contractId,
                Name = "Standard",
                NightRate = null,
                HolidayRate = 0m,
                PerformsShiftWork = null,
                ValidFrom = new DateTime(2026, 1, 1)
            });
        var skill = new GetContractDetailsSkill(mediator, SourceResolver());

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object>
        {
            ["contractId"] = contractId.ToString()
        });

        result.Success.ShouldBeTrue();
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("NightRate").ValueKind.ShouldBe(JsonValueKind.Null);
        data.GetProperty("HolidayRate").GetDecimal().ShouldBe(0m);
        data.GetProperty("PerformsShiftWork").ValueKind.ShouldBe(JsonValueKind.Null);
        data.GetProperty("StandardValueNote").GetString()!.ShouldContain("null");
    }
    private static IHolidayCalendarSourceResolver SourceResolver(ResolvedHolidayCalendarSource? resolved = null)
    {
        var resolver = Substitute.For<IHolidayCalendarSourceResolver>();
        resolver.ResolveAsync(Arg.Any<Guid?>(), Arg.Any<CancellationToken>())
            .Returns(resolved ?? new ResolvedHolidayCalendarSource(HolidayCalendarSource.None, null, null, null, null));
        return resolver;
    }

    [Test]
    public async Task HolidayCalendar_IsReportedByNameAndSource_NotById()
    {
        var contractId = Guid.NewGuid();
        var selectionId = Guid.NewGuid();
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(new ContractResource { Id = contractId, Name = "Botschaft", CalendarSelectionId = selectionId, ValidFrom = new DateTime(2026, 1, 1) });
        var resolver = SourceResolver(new ResolvedHolidayCalendarSource(HolidayCalendarSource.Contract, selectionId, "Bern + USA", null, null));
        var skill = new GetContractDetailsSkill(mediator, resolver);

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["contractId"] = contractId.ToString() });

        result.Success.ShouldBeTrue();
        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("HolidayCalendarName").GetString().ShouldBe("Bern + USA");
        data.GetProperty("HolidayCalendarSource").GetString().ShouldBe(nameof(HolidayCalendarSource.Contract));
        data.TryGetProperty("CalendarSelectionId", out _).ShouldBeFalse();
        await resolver.Received(1).ResolveAsync(selectionId, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task HolidayCalendar_WithoutOwnSelection_ReportsCompanyFallback()
    {
        var mediator = Substitute.For<IMediator>();
        mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns(new ContractResource { Id = Guid.NewGuid(), Name = "Standard", ValidFrom = new DateTime(2026, 1, 1) });
        var skill = new GetContractDetailsSkill(mediator,
            SourceResolver(new ResolvedHolidayCalendarSource(HolidayCalendarSource.CompanyCountryState, null, null, "CH", "BE")));

        var result = await skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["contractId"] = Guid.NewGuid().ToString() });

        var data = JsonSerializer.SerializeToElement(result.Data);
        data.GetProperty("HolidayCalendarSource").GetString().ShouldBe(nameof(HolidayCalendarSource.CompanyCountryState));
        data.GetProperty("HolidayCalendarName").GetString().ShouldBe("CH-BE");
    }
}
