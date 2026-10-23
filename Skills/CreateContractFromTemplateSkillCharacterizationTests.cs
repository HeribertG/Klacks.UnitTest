// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Characterization tests for CreateContractFromTemplateSkill.ExecuteAsync, written before its long body was split
/// into private steps: the exact refusal texts in their precedence order (template reference, name, validFrom, the
/// company clock read, the override and workload-path checks, payment interval, workdays, template load, duplicate
/// name, individual pay period, region, validation, create), and how far the company clock, the template query,
/// the contract list, the calendar lookup and the create command are reached before each refusal.
/// </summary>

using Klacks.Api.Application.Commands;
using Klacks.Api.Application.DTOs.Associations;
using Klacks.Api.Application.Queries;
using Klacks.Api.Application.Services.Contracts;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Infrastructure.Mediator;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CreateContractFromTemplateSkillCharacterizationTests
{
    private const string NewName = "Vollzeit 120 BE";

    private static readonly Guid TemplateId = Guid.Parse("f0000000-0000-0000-0000-000000000001");
    private static readonly Guid CalendarId = Guid.Parse("f0000000-0000-0000-0000-000000000002");

    private IMediator _mediator = null!;
    private ICompanyClock _companyClock = null!;
    private IContractRepository _contractRepository = null!;
    private ICalendarSelectionRepository _calendarSelectionRepository = null!;
    private ICountryResolver _countryResolver = null!;
    private CreateContractFromTemplateSkill _skill = null!;
    private List<string> _calls = null!;
    private List<Contract> _existing = null!;
    private bool _templateMissing;

    [SetUp]
    public void Setup()
    {
        _calls = [];
        _existing = [];
        _templateMissing = false;

        _mediator = Substitute.For<IMediator>();
        _mediator.Send(Arg.Any<GetQuery<ContractResource>>(), Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _calls.Add("template-get");
            if (_templateMissing)
            {
                throw new KeyNotFoundException();
            }

            return Template();
        });
        _mediator.Send(Arg.Any<PostCommand<ContractResource>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            _calls.Add("post");
            var created = ContractTemplateCopier.CopyAsNew(call.ArgAt<PostCommand<ContractResource>>(0).Resource);
            created.Id = Guid.NewGuid();
            return created;
        });

        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(_ =>
        {
            _calls.Add("clock");
            return new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc);
        });

        _contractRepository = Substitute.For<IContractRepository>();
        _contractRepository.List().Returns(_ => { _calls.Add("list"); return _existing; });

        _calendarSelectionRepository = Substitute.For<ICalendarSelectionRepository>();
        _calendarSelectionRepository.List().Returns(new List<CalendarSelection>());
        _calendarSelectionRepository
            .GetIdsByStateAsync(Arg.Any<string>(), Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(call =>
            {
                _calls.Add("calendar");
                return call.ArgAt<string>(1) == "ZH" ? new List<Guid> { CalendarId } : new List<Guid>();
            });

        _countryResolver = Substitute.For<ICountryResolver>();
        _countryResolver.GetDefaultAsync(Arg.Any<CancellationToken>()).Returns(new Countries { Abbreviation = "CH" });

        _skill = new CreateContractFromTemplateSkill(
            _mediator, _companyClock, _contractRepository, _calendarSelectionRepository, _countryResolver);
    }

    private static ContractResource Template() => new()
    {
        Id = TemplateId,
        Name = "Vollzeit 160 BE",
        GuaranteedHours = 160m,
        MinimumHours = 120m,
        MaximumHours = 200m,
        FullTime = 180m,
        PaymentInterval = PaymentInterval.Monthly,
        ValidFrom = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc)
    };

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "admin",
        UserPermissions = new List<string> { Permissions.CanCreateContracts }
    };

    private Task<SkillResult> Run(params (string Key, object? Value)[] overrides)
    {
        var parameters = new Dictionary<string, object>
        {
            [ContractFieldNames.TemplateContractId] = TemplateId.ToString(),
            [ContractFieldNames.Name] = NewName,
            [ContractFieldNames.ValidFrom] = "2026-07-01"
        };
        foreach (var (key, value) in overrides)
        {
            if (value is null)
            {
                parameters.Remove(key);
            }
            else
            {
                parameters[key] = value;
            }
        }

        return _skill.ExecuteAsync(Ctx(), parameters);
    }

    [Test]
    public async Task Success_ReachesClockTemplateListAndPostInThisOrder()
    {
        var result = await Run((ContractFieldNames.Region, "zh"));

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldStartWith($"Contract '{NewName}' created (id ");
        _calls.ShouldBe(["clock", "template-get", "list", "calendar", "post"]);
    }

    [Test]
    public async Task MissingTemplateReference_IsRefusedFirst()
    {
        var result = await Run((ContractFieldNames.TemplateContractId, null), (ContractFieldNames.Name, null));

        result.Message.ShouldBe(
            $"Missing required parameter '{ContractFieldNames.TemplateContractId}' (contract id or exact contract name).");
        _calls.ShouldBeEmpty();
    }

    [Test]
    public async Task MissingName_IsRefusedBeforeValidFromAndTheClock()
    {
        var result = await Run((ContractFieldNames.Name, "   "), (ContractFieldNames.ValidFrom, null));

        result.Success.ShouldBeFalse();
        result.Message.ShouldBe("Missing required parameter 'name'.");
        _calls.ShouldBeEmpty();
    }

    [Test]
    public async Task MissingValidFrom_IsRefusedBeforeTheClockIsRead()
    {
        var result = await Run((ContractFieldNames.ValidFrom, "  "), (ContractFieldNames.GuaranteedHours, "x"));

        result.Message.ShouldBe("Missing required parameter 'validFrom' (YYYY-MM-DD).");
        _calls.ShouldBeEmpty();
    }

    [Test]
    public async Task InvalidValidFrom_IsRefusedAfterTheClockRead_BeforeTheOverrides()
    {
        var result = await Run((ContractFieldNames.ValidFrom, "not-a-date"), (ContractFieldNames.GuaranteedHours, "x"));

        result.Message.ShouldBe(
            "Invalid validFrom value: 'not-a-date'. Please give a concrete date (for example 2026-05-01), " +
            "a relative day such as 'today' or 'tomorrow', or ask the user which date is meant.");
        _calls.ShouldBe(["clock"]);
    }

    [Test]
    public async Task UnreadableOverride_IsRefusedBeforeTheWorkloadPathCheck()
    {
        var result = await Run(
            (ContractFieldNames.FullTime, "40 hours"),
            (ContractFieldNames.GuaranteedHours, 120m),
            (ContractFieldNames.Percent, 80m));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain($"'{ContractFieldNames.FullTime}'");
        result.Message.ShouldNotContain("mutually exclusive");
        _calls.ShouldBe(["clock"]);
    }

    [Test]
    public async Task BothWorkloadPaths_AreRefusedBeforeThePaymentInterval()
    {
        var result = await Run(
            (ContractFieldNames.GuaranteedHours, 120m),
            (ContractFieldNames.Percent, 80m),
            (ContractFieldNames.PaymentInterval, "Quarterly"));

        result.Message.ShouldBe(
            "Parameters 'guaranteedHours' and 'percent' are mutually exclusive: fixed guaranteed hours or a " +
            "workload percent of the company-wide value. Ask the administrator which one applies.");
        _calls.ShouldBe(["clock"]);
    }

    [Test]
    public async Task MalformedPaymentInterval_IsRefusedBeforeTheWorkdays()
    {
        var result = await Run(
            (ContractFieldNames.PaymentInterval, "Quarterly"),
            (ContractFieldNames.Workdays, "Funday"));

        result.Message!.ShouldContain("Invalid paymentInterval");
        result.Message.ShouldNotContain("Funday");
        _calls.ShouldBe(["clock"]);
    }

    [Test]
    public async Task MalformedWorkdays_AreRefusedBeforeTheTemplateIsLoaded()
    {
        _templateMissing = true;

        var result = await Run((ContractFieldNames.Workdays, "Mon,Funday"));

        result.Message!.ShouldContain("Funday");
        _calls.ShouldBe(["clock"]);
    }

    [Test]
    public async Task UnknownTemplate_IsRefusedBeforeTheDuplicateCheck()
    {
        _templateMissing = true;
        _existing = [new Contract { Id = Guid.NewGuid(), Name = NewName }];

        var result = await Run();

        result.Message.ShouldBe($"Template contract '{TemplateId}' not found.");
        _calls.ShouldBe(["clock", "template-get"]);
    }

    [Test]
    public async Task DuplicateName_TrimmedAndCaseInsensitive_IsRefusedBeforeThePaymentIntervalIsApplied()
    {
        _existing = [new Contract { Id = Guid.NewGuid(), Name = "  vollzeit 120 be " }];

        var result = await Run((ContractFieldNames.PaymentInterval, "Individual"));

        result.Message.ShouldBe(
            $"A contract named '{NewName}' already exists. Ask the administrator for a different name — " +
            "no suffix is added automatically.");
        _calls.ShouldBe(["clock", "template-get", "list"]);
    }

    [Test]
    public async Task IndividualIntervalWithoutPeriod_IsRefusedBeforeTheRegionIsResolved()
    {
        var result = await Run(
            (ContractFieldNames.PaymentInterval, "Individual"),
            (ContractFieldNames.Region, "zh"));

        result.Message.ShouldBe(
            "The template has no individual pay period, so 'paymentInterval' cannot be switched to Individual. " +
            "Choose another interval or a template that already has one.");
        _calls.ShouldBe(["clock", "template-get", "list"]);
    }

    [Test]
    public async Task UnknownRegion_IsRefusedBeforeTheValidator()
    {
        var result = await Run(
            (ContractFieldNames.Region, "XX"),
            (ContractFieldNames.Percent, 80m),
            (ContractFieldNames.MinimumHours, 10m));

        result.Success.ShouldBeFalse();
        result.Message!.ShouldContain("No holiday calendar");
        result.Message.ShouldNotStartWith("The adapted contract would be invalid");
        _calls.ShouldBe(["clock", "template-get", "list", "calendar"]);
    }

    [Test]
    public async Task ValidatorViolation_IsRefusedWithItsPrefix_AndNothingIsPosted()
    {
        var result = await Run((ContractFieldNames.Percent, 80m), (ContractFieldNames.MinimumHours, 10m));

        result.Success.ShouldBeFalse();
        result.Message.ShouldStartWith("The adapted contract would be invalid: ");
        _calls.ShouldBe(["clock", "template-get", "list"]);
    }

    [Test]
    public async Task NullCreateResult_HasItsExactText()
    {
        _mediator.Send(Arg.Any<PostCommand<ContractResource>>(), Arg.Any<CancellationToken>())
            .Returns((ContractResource?)null);

        var result = await Run();

        result.Message.ShouldBe($"Contract '{NewName}' could not be created.");
    }

    [Test]
    public async Task PostedResource_CarriesTheTrimmedNameValidFromAndAllOverrides()
    {
        ContractResource? posted = null;
        _mediator.Send(Arg.Any<PostCommand<ContractResource>>(), Arg.Any<CancellationToken>()).Returns(call =>
        {
            posted = call.ArgAt<PostCommand<ContractResource>>(0).Resource;
            var created = ContractTemplateCopier.CopyAsNew(posted);
            created.Id = Guid.NewGuid();
            return created;
        });

        var result = await Run(
            (ContractFieldNames.Name, "  " + NewName + "  "),
            (ContractFieldNames.GuaranteedHours, 140m),
            (ContractFieldNames.FullTime, 168m),
            (ContractFieldNames.PerformsShiftWork, false),
            (ContractFieldNames.PaymentInterval, "weekly"),
            (ContractFieldNames.Workdays, "Mon,Tue"),
            (ContractFieldNames.Region, "ZH"));

        result.Success.ShouldBeTrue(result.Message);
        posted.ShouldNotBeNull();
        posted.Name.ShouldBe(NewName);
        posted.ValidFrom.ShouldBe(new DateTime(2026, 7, 1, 0, 0, 0, DateTimeKind.Utc));
        posted.GuaranteedHours.ShouldBe(140m);
        posted.MinimumHours.ShouldBe(140m);
        posted.MaximumHours.ShouldBe(140m);
        posted.FullTime.ShouldBe(168m);
        posted.PerformsShiftWork.ShouldBe(false);
        posted.PaymentInterval.ShouldBe(PaymentInterval.Weekly);
        posted.WorkOnMonday.ShouldBeTrue();
        posted.WorkOnWednesday.ShouldBeFalse();
        posted.CalendarSelectionId.ShouldBe(CalendarId);
    }
}
