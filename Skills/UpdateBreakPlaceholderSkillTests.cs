// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for UpdateBreakPlaceholderSkill — verifies move/resize/convert of a planned absence
/// including database verification (verify case), rollback on failed verification, resolution by
/// clientId + date with ambiguity handling, and validation errors without any write.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class UpdateBreakPlaceholderSkillTests
{
    private IBreakPlaceholderRepository _breakPlaceholderRepository = null!;
    private IAbsenceRepository _absenceRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private UpdateBreakPlaceholderSkill _skill = null!;

    private static readonly Guid PlaceholderId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();
    private static readonly Guid AbsenceId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _breakPlaceholderRepository = Substitute.For<IBreakPlaceholderRepository>();
        _absenceRepository = Substitute.For<IAbsenceRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.Arg<Func<Task<bool>>>()());

        _skill = new UpdateBreakPlaceholderSkill(
            _breakPlaceholderRepository, _absenceRepository, _unitOfWork);
    }

    private static SkillExecutionContext Context() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.Empty,
        UserName = "tester",
        UserPermissions = []
    };

    private static BreakPlaceholder Placeholder() => new()
    {
        Id = PlaceholderId,
        ClientId = ClientId,
        AbsenceId = AbsenceId,
        From = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc),
        Until = new DateTime(2026, 8, 7, 0, 0, 0, DateTimeKind.Utc)
    };

    [Test]
    public async Task MoveById_PersistsAndVerifies()
    {
        var tracked = Placeholder();
        _breakPlaceholderRepository.Get(PlaceholderId).Returns(tracked);
        _breakPlaceholderRepository.GetNoTracking(PlaceholderId).Returns(_ => Task.FromResult<BreakPlaceholder?>(tracked));

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            ["placeholderId"] = PlaceholderId.ToString(),
            ["newFromDate"] = "2026-08-10",
            ["newUntilDate"] = "2026-08-14"
        });

        result.Success.ShouldBeTrue(result.Message);
        tracked.From.ShouldBe(new DateTime(2026, 8, 10, 0, 0, 0, DateTimeKind.Utc));
        tracked.Until.ShouldBe(new DateTime(2026, 8, 14, 0, 0, 0, DateTimeKind.Utc));
        result.Message.ShouldContain("verified");
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task VerificationMismatch_ThrowsAndReportsRollback()
    {
        var tracked = Placeholder();
        var stale = Placeholder();
        _breakPlaceholderRepository.Get(PlaceholderId).Returns(tracked);
        _breakPlaceholderRepository.GetNoTracking(PlaceholderId).Returns(Task.FromResult<BreakPlaceholder?>(stale));

        stale.From = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);

        await Should.ThrowAsync<SkillVerificationException>(() =>
            _skill.ExecuteAsync(Context(), new Dictionary<string, object>
            {
                ["placeholderId"] = PlaceholderId.ToString(),
                ["newFromDate"] = "2026-08-10",
                ["newUntilDate"] = "2026-08-14"
            }));
    }

    [Test]
    public async Task ConvertType_UnknownAbsence_ReturnsError_WithoutWrite()
    {
        _breakPlaceholderRepository.Get(PlaceholderId).Returns(Placeholder());
        _absenceRepository.Exists(Arg.Any<Guid>()).Returns(false);

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            ["placeholderId"] = PlaceholderId.ToString(),
            ["absenceId"] = Guid.NewGuid().ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("list_absence_types");
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task NothingToChange_ReturnsError()
    {
        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            ["placeholderId"] = PlaceholderId.ToString()
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Nothing to change");
    }

    [Test]
    public async Task ResultingRangeInvalid_ReturnsError_WithoutWrite()
    {
        _breakPlaceholderRepository.Get(PlaceholderId).Returns(Placeholder());

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            ["placeholderId"] = PlaceholderId.ToString(),
            ["newFromDate"] = "2026-08-20"
        });

        result.Success.ShouldBeFalse();
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task ResolveByClientAndDate_AmbiguousMatches_ReturnsOptions()
    {
        var first = Placeholder();
        var second = Placeholder();
        second.Id = Guid.NewGuid();
        _breakPlaceholderRepository
            .GetByClientAndRangeAsync(ClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<BreakPlaceholder> { first, second });

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["date"] = "2026-08-04",
            ["newFromDate"] = "2026-08-10"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("Multiple planned absences");
        result.Message.ShouldContain(first.Id.ToString());
        result.Message.ShouldContain(second.Id.ToString());
    }

    [Test]
    public async Task ResolveByClientAndDate_LoneMatch_Updates()
    {
        var lone = Placeholder();
        _breakPlaceholderRepository
            .GetByClientAndRangeAsync(ClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<BreakPlaceholder> { lone });
        _breakPlaceholderRepository.Get(PlaceholderId).Returns(lone);
        _breakPlaceholderRepository.GetNoTracking(PlaceholderId).Returns(_ => Task.FromResult<BreakPlaceholder?>(lone));

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["date"] = "2026-08-04",
            ["newUntilDate"] = "2026-08-21"
        });

        result.Success.ShouldBeTrue(result.Message);
        lone.Until.ShouldBe(new DateTime(2026, 8, 21, 0, 0, 0, DateTimeKind.Utc));
    }
}
