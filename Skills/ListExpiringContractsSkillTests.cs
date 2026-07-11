// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for ListExpiringContractsSkill: empty result, the within/outside-window boundary for
/// contracts (via the correct GetExpiringBetweenAsync horizon) and for qualifications (filtered
/// in-memory), the includeQualifications toggle, entity-type filtering (Customer excluded) and the
/// combined ascending sort by ValidUntil.
/// </summary>

using Klacks.Api.Application.DTOs.Staffs;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Common;
using Klacks.Api.Domain.Enums;
using Klacks.Api.Domain.Interfaces.Assistant;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Interfaces.Settings;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Models.Staffs;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ListExpiringContractsSkillTests
{
    private static readonly DateTime TodayUtc = new(2026, 7, 10, 0, 0, 0, DateTimeKind.Utc);
    private static readonly DateOnly Today = DateOnly.FromDateTime(TodayUtc);

    private IClientContractReadRepository _clientContractReadRepository = null!;
    private IContractRepository _contractRepository = null!;
    private IClientQualificationRepository _clientQualificationRepository = null!;
    private IQualificationRepository _qualificationRepository = null!;
    private IClientRepository _clientRepository = null!;
    private ICompanyClock _companyClock = null!;
    private ListExpiringContractsSkill _skill = null!;

    [SetUp]
    public void Setup()
    {
        _clientContractReadRepository = Substitute.For<IClientContractReadRepository>();
        _contractRepository = Substitute.For<IContractRepository>();
        _clientQualificationRepository = Substitute.For<IClientQualificationRepository>();
        _qualificationRepository = Substitute.For<IQualificationRepository>();
        _clientRepository = Substitute.For<IClientRepository>();
        _companyClock = Substitute.For<ICompanyClock>();
        _companyClock.GetTodayAsync(Arg.Any<CancellationToken>()).Returns(TodayUtc);

        _clientContractReadRepository
            .GetExpiringBetweenAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract>());
        _clientQualificationRepository.List().Returns(new List<ClientQualification>());

        _skill = new ListExpiringContractsSkill(
            _clientContractReadRepository,
            _contractRepository,
            _clientQualificationRepository,
            _qualificationRepository,
            _clientRepository,
            _companyClock);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string> { "CanViewClients" }
    };

    private static ClientContract MakeContract(
        Guid clientId, DateOnly untilDate, Guid contractId, EntityTypeEnum type = EntityTypeEnum.Employee, bool isActive = true) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        ContractId = contractId,
        FromDate = untilDate.AddYears(-1),
        UntilDate = untilDate,
        IsActive = isActive,
        Client = new Client { Id = clientId, FirstName = "Max", Name = "Muster", Type = type }
    };

    private static ClientQualification MakeQualification(
        Guid clientId, Guid qualificationId, DateOnly? validUntil) => new()
    {
        Id = Guid.NewGuid(),
        ClientId = clientId,
        QualificationId = qualificationId,
        Level = QualificationLevel.Basic,
        ValidUntil = validUntil
    };

    [Test]
    public async Task ExecuteAsync_NothingExpiring_ReturnsEmptyResultWithMessage()
    {
        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        result.Success.ShouldBeTrue();
        var data = (ListExpiringContractsResult)result.Data!;
        data.Items.ShouldBeEmpty();
        data.TotalContractCount.ShouldBe(0);
        data.TotalQualificationCount.ShouldBe(0);
        result.Message.ShouldContain("No contracts or qualifications expire");
    }

    [Test]
    public async Task ExecuteAsync_UsesWithinDays_ToComputeContractHorizon()
    {
        var parameters = new Dictionary<string, object> { ["withinDays"] = 30 };

        await _skill.ExecuteAsync(Ctx(), parameters);

        await _clientContractReadRepository.Received(1).GetExpiringBetweenAsync(
            Today, Today.AddDays(30), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_ContractExpiringInsideWindow_IsIncluded()
    {
        var clientId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var contract = MakeContract(clientId, Today.AddDays(10), contractId);

        _clientContractReadRepository
            .GetExpiringBetweenAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract> { contract });
        _contractRepository.List().Returns(new List<Contract> { new() { Id = contractId, Name = "180 BE" } });

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["withinDays"] = 90 });

        var data = (ListExpiringContractsResult)result.Data!;
        data.TotalContractCount.ShouldBe(1);
        var item = data.Items.Single();
        item.Kind.ShouldBe("Contract");
        item.Name.ShouldBe("180 BE");
        item.ClientName.ShouldBe("Max Muster");
        item.DaysUntilExpiry.ShouldBe(10);
    }

    [Test]
    public async Task ExecuteAsync_ContractForCustomer_IsExcluded()
    {
        var clientId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var contract = MakeContract(clientId, Today.AddDays(5), contractId, EntityTypeEnum.Customer);

        _clientContractReadRepository
            .GetExpiringBetweenAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract> { contract });
        _contractRepository.List().Returns(new List<Contract> { new() { Id = contractId, Name = "180 BE" } });

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var data = (ListExpiringContractsResult)result.Data!;
        data.TotalContractCount.ShouldBe(0);
        data.Items.ShouldBeEmpty();
    }

    [Test]
    public async Task ExecuteAsync_SupersededContract_IsExcluded()
    {
        var clientId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var supersededContract = MakeContract(clientId, Today.AddDays(5), contractId, isActive: false);

        _clientContractReadRepository
            .GetExpiringBetweenAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract> { supersededContract });
        _contractRepository.List().Returns(new List<Contract> { new() { Id = contractId, Name = "180 BE" } });

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object>());

        var data = (ListExpiringContractsResult)result.Data!;
        data.TotalContractCount.ShouldBe(0);
        data.Items.ShouldBeEmpty();
    }

    [Test]
    public async Task ExecuteAsync_QualificationOutsideWindow_IsExcluded()
    {
        var clientId = Guid.NewGuid();
        var qualificationId = Guid.NewGuid();

        var withinWindow = MakeQualification(clientId, qualificationId, Today.AddDays(20));
        var outsideWindow = MakeQualification(clientId, qualificationId, Today.AddDays(200));
        var alreadyExpired = MakeQualification(clientId, qualificationId, Today.AddDays(-5));

        _clientQualificationRepository.List()
            .Returns(new List<ClientQualification> { withinWindow, outsideWindow, alreadyExpired });
        _clientRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client> { new() { Id = clientId, FirstName = "Anna", Name = "Beispiel", Type = EntityTypeEnum.Employee } });
        _qualificationRepository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Qualification> { new() { Id = qualificationId, Name = new MultiLanguage { De = "Stapler" } } });

        var result = await _skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["withinDays"] = 90 });

        var data = (ListExpiringContractsResult)result.Data!;
        data.TotalQualificationCount.ShouldBe(1);
        var item = data.Items.Single();
        item.Kind.ShouldBe("Qualification");
        item.Name.ShouldBe("Stapler");
        item.DaysUntilExpiry.ShouldBe(20);
    }

    [Test]
    public async Task ExecuteAsync_IncludeQualificationsFalse_SkipsQualificationLookup()
    {
        var clientId = Guid.NewGuid();
        var qualificationId = Guid.NewGuid();
        var withinWindow = MakeQualification(clientId, qualificationId, Today.AddDays(20));
        _clientQualificationRepository.List().Returns(new List<ClientQualification> { withinWindow });

        var result = await _skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["includeQualifications"] = false });

        var data = (ListExpiringContractsResult)result.Data!;
        data.IncludeQualifications.ShouldBeFalse();
        data.TotalQualificationCount.ShouldBe(0);
        data.Items.ShouldBeEmpty();
        await _clientRepository.DidNotReceive().GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ExecuteAsync_CombinesContractsAndQualifications_SortedByValidUntilAscending()
    {
        var clientId = Guid.NewGuid();
        var contractId = Guid.NewGuid();
        var qualificationId = Guid.NewGuid();

        var laterContract = MakeContract(clientId, Today.AddDays(40), contractId);
        var earlierQualification = MakeQualification(clientId, qualificationId, Today.AddDays(5));

        _clientContractReadRepository
            .GetExpiringBetweenAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract> { laterContract });
        _contractRepository.List().Returns(new List<Contract> { new() { Id = contractId, Name = "180 BE" } });

        _clientQualificationRepository.List().Returns(new List<ClientQualification> { earlierQualification });
        _clientRepository.GetByIdsAsync(Arg.Any<IEnumerable<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new List<Client> { new() { Id = clientId, FirstName = "Max", Name = "Muster", Type = EntityTypeEnum.Employee } });
        _qualificationRepository.GetAllAsync(Arg.Any<CancellationToken>())
            .Returns(new List<Qualification> { new() { Id = qualificationId, Name = new MultiLanguage { De = "Stapler" } } });

        var result = await _skill.ExecuteAsync(Ctx(), new Dictionary<string, object> { ["withinDays"] = 90 });

        var data = (ListExpiringContractsResult)result.Data!;
        data.Items.Select(i => i.Kind).ShouldBe(new[] { "Qualification", "Contract" });
        data.Items[0].ValidUntil.ShouldBe(Today.AddDays(5));
        data.Items[1].ValidUntil.ShouldBe(Today.AddDays(40));
    }

    [Test]
    public async Task ExecuteAsync_LimitAppliesToCombinedResult_ButTotalsReflectAllMatches()
    {
        var contractId = Guid.NewGuid();
        var firstClientId = Guid.NewGuid();
        var secondClientId = Guid.NewGuid();
        var contract1 = MakeContract(firstClientId, Today.AddDays(5), contractId);
        var contract2 = MakeContract(secondClientId, Today.AddDays(6), contractId);

        _clientContractReadRepository
            .GetExpiringBetweenAsync(Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<ClientContract> { contract1, contract2 });
        _contractRepository.List().Returns(new List<Contract> { new() { Id = contractId, Name = "180 BE" } });

        var result = await _skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["limit"] = 1 });

        var data = (ListExpiringContractsResult)result.Data!;
        data.TotalContractCount.ShouldBe(2);
        data.Items.Count.ShouldBe(1);
    }

    [Test]
    public async Task ExecuteAsync_NonPositiveWithinDays_ReturnsError()
    {
        var result = await _skill.ExecuteAsync(
            Ctx(), new Dictionary<string, object> { ["withinDays"] = 0 });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("withinDays");
    }
}
