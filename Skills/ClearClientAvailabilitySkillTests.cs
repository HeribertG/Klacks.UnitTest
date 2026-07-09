// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for clear_client_availability — verifies removal with database verification (verify
/// case), rollback when records are still visible, the no-op path for empty ranges, and range
/// validation without any write.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Staffs;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ClearClientAvailabilitySkillTests
{
    private IClientAvailabilityRepository _availabilityRepository = null!;
    private IClientRepository _clientRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ClearClientAvailabilitySkill _skill = null!;

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly DateOnly Start = new(2026, 8, 3);
    private static readonly DateOnly End = new(2026, 8, 7);

    [SetUp]
    public void SetUp()
    {
        _availabilityRepository = Substitute.For<IClientAvailabilityRepository>();
        _clientRepository = Substitute.For<IClientRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _clientRepository.Exists(ClientId).Returns(true);
        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.Arg<Func<Task<bool>>>()());

        _skill = new ClearClientAvailabilitySkill(_availabilityRepository, _clientRepository, _unitOfWork);
    }

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = []
    };

    private static Dictionary<string, object> Parameters(string start = "2026-08-03", string end = "2026-08-07") => new()
    {
        ["clientId"] = ClientId.ToString(),
        ["startDate"] = start,
        ["endDate"] = end
    };

    private static List<ClientAvailability> Entries(int count) =>
        Enumerable.Range(8, count).Select(hour => new ClientAvailability
        {
            Id = Guid.NewGuid(),
            ClientId = ClientId,
            Date = Start,
            Hour = hour,
            IsAvailable = true
        }).ToList();

    [Test]
    public async Task ExistingRecords_AreRemovedAndVerified()
    {
        var entries = Entries(3);
        _availabilityRepository.GetByClientAndDateRange(ClientId, Start, End)
            .Returns(Task.FromResult(entries), Task.FromResult(new List<ClientAvailability>()));

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("verified");
        result.Message.ShouldContain("fully open");
        foreach (var entry in entries)
        {
            _availabilityRepository.Received(1).Remove(entry);
        }

        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task RecordsStillVisibleAfterDelete_ThrowsForRollback()
    {
        var entries = Entries(2);
        _availabilityRepository.GetByClientAndDateRange(ClientId, Start, End)
            .Returns(Task.FromResult(entries), Task.FromResult(entries));

        await Should.ThrowAsync<SkillVerificationException>(() =>
            _skill.ExecuteAsync(Context(), Parameters()));
    }

    [Test]
    public async Task EmptyRange_ReturnsNoOpWithoutWrite()
    {
        _availabilityRepository.GetByClientAndDateRange(ClientId, Start, End)
            .Returns(Task.FromResult(new List<ClientAvailability>()));

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("already fully open");
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task EndBeforeStart_ReturnsError()
    {
        var result = await _skill.ExecuteAsync(Context(), Parameters(start: "2026-08-07", end: "2026-08-03"));

        result.Success.ShouldBeFalse();
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task RangeTooLarge_ReturnsError()
    {
        var result = await _skill.ExecuteAsync(Context(), Parameters(start: "2026-01-01", end: "2026-12-31"));

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("92");
    }

    [Test]
    public async Task UnknownClient_ReturnsError()
    {
        _clientRepository.Exists(ClientId).Returns(false);

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
    }
}
