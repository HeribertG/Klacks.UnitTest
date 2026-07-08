// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for AddBreakPlaceholderSkill — verifies the placeholder is persisted over the full
/// range, and that invalid ranges, unknown clients and unknown absence types return errors
/// without writing anything.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class AddBreakPlaceholderSkillTests
{
    private IBreakPlaceholderRepository _breakPlaceholderRepository = null!;
    private IAbsenceRepository _absenceRepository = null!;
    private IClientRepository _clientRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private AddBreakPlaceholderSkill _skill = null!;

    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid AbsenceId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _breakPlaceholderRepository = Substitute.For<IBreakPlaceholderRepository>();
        _absenceRepository = Substitute.For<IAbsenceRepository>();
        _clientRepository = Substitute.For<IClientRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _clientRepository.Exists(ClientId).Returns(true);
        _absenceRepository.Exists(AbsenceId).Returns(true);

        _skill = new AddBreakPlaceholderSkill(
            _breakPlaceholderRepository, _absenceRepository, _clientRepository, _unitOfWork);
    }

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = []
    };

    private static Dictionary<string, object> Parameters(string from = "2026-08-03", string until = "2026-08-14") => new()
    {
        ["clientId"] = ClientId.ToString(),
        ["absenceId"] = AbsenceId.ToString(),
        ["fromDate"] = from,
        ["untilDate"] = until,
        ["information"] = "vacation wish from email"
    };

    [Test]
    public async Task ValidRange_PersistsPlaceholderAndCompletes()
    {
        BreakPlaceholder? added = null;
        await _breakPlaceholderRepository.Add(Arg.Do<BreakPlaceholder>(p => added = p));

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeTrue(result.Message);
        added.ShouldNotBeNull();
        added!.ClientId.ShouldBe(ClientId);
        added.AbsenceId.ShouldBe(AbsenceId);
        added.From.ShouldBe(new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc));
        added.Until.ShouldBe(new DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc));
        added.Information.ShouldBe("vacation wish from email");
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task UntilBeforeFrom_ReturnsError_WithoutWrite()
    {
        var result = await _skill.ExecuteAsync(Context(), Parameters(from: "2026-08-14", until: "2026-08-03"));

        result.Success.ShouldBeFalse();
        await _breakPlaceholderRepository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Test]
    public async Task UnknownClient_ReturnsError()
    {
        _clientRepository.Exists(ClientId).Returns(false);

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("not found");
        await _breakPlaceholderRepository.DidNotReceiveWithAnyArgs().Add(default!);
    }

    [Test]
    public async Task UnknownAbsenceType_ReturnsError_MentioningListSkill()
    {
        _absenceRepository.Exists(AbsenceId).Returns(false);

        var result = await _skill.ExecuteAsync(Context(), Parameters());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("list_absence_types");
        await _breakPlaceholderRepository.DidNotReceiveWithAnyArgs().Add(default!);
    }
}
