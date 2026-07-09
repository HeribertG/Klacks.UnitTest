// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for DeleteBreakPlaceholderSkill — verifies the soft-delete with database verification
/// (verify case), rollback when the row is still visible, the booked-absence hint pointing to
/// delete_break, and resolution errors without any write.
/// </summary>

using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Exceptions;
using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Models.Assistant;
using Klacks.Api.Domain.Models.Schedules;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class DeleteBreakPlaceholderSkillTests
{
    private IBreakPlaceholderRepository _breakPlaceholderRepository = null!;
    private IBreakRepository _breakRepository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private DeleteBreakPlaceholderSkill _skill = null!;

    private static readonly Guid PlaceholderId = Guid.NewGuid();
    private static readonly Guid ClientId = Guid.NewGuid();

    [SetUp]
    public void SetUp()
    {
        _breakPlaceholderRepository = Substitute.For<IBreakPlaceholderRepository>();
        _breakRepository = Substitute.For<IBreakRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();

        _unitOfWork.ExecuteInTransactionAsync(Arg.Any<Func<Task<bool>>>())
            .Returns(ci => ci.Arg<Func<Task<bool>>>()());

        _skill = new DeleteBreakPlaceholderSkill(
            _breakPlaceholderRepository, _breakRepository, _unitOfWork);
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
        AbsenceId = Guid.NewGuid(),
        From = new DateTime(2026, 8, 3, 0, 0, 0, DateTimeKind.Utc),
        Until = new DateTime(2026, 8, 7, 0, 0, 0, DateTimeKind.Utc)
    };

    [Test]
    public async Task DeleteById_RemovesAndVerifies()
    {
        _breakPlaceholderRepository.Get(PlaceholderId).Returns(Placeholder());
        _breakPlaceholderRepository.GetNoTracking(PlaceholderId).Returns(Task.FromResult<BreakPlaceholder?>(null));

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            ["placeholderId"] = PlaceholderId.ToString()
        });

        result.Success.ShouldBeTrue(result.Message);
        result.Message.ShouldContain("verified");
        await _breakPlaceholderRepository.Received(1).Delete(PlaceholderId);
        await _unitOfWork.Received(1).CompleteAsync();
    }

    [Test]
    public async Task RowStillVisibleAfterDelete_ThrowsForRollback()
    {
        _breakPlaceholderRepository.Get(PlaceholderId).Returns(Placeholder());
        _breakPlaceholderRepository.GetNoTracking(PlaceholderId).Returns(Task.FromResult<BreakPlaceholder?>(Placeholder()));

        await Should.ThrowAsync<SkillVerificationException>(() =>
            _skill.ExecuteAsync(Context(), new Dictionary<string, object>
            {
                ["placeholderId"] = PlaceholderId.ToString()
            }));
    }

    [Test]
    public async Task NoPlaceholderButBookedBreak_ReturnsDeleteBreakHint()
    {
        var bookedId = Guid.NewGuid();
        _breakPlaceholderRepository
            .GetByClientAndRangeAsync(ClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<BreakPlaceholder>());
        _breakRepository
            .GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<Break> { new() { Id = bookedId, ClientId = ClientId, CurrentDate = new DateOnly(2026, 8, 4) } });

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["date"] = "2026-08-04"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("delete_break");
        result.Message.ShouldContain(bookedId.ToString());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task NothingFound_ReturnsError_WithoutWrite()
    {
        _breakPlaceholderRepository
            .GetByClientAndRangeAsync(ClientId, Arg.Any<DateTime>(), Arg.Any<DateTime>(), Arg.Any<CancellationToken>())
            .Returns(new List<BreakPlaceholder>());
        _breakRepository
            .GetByClientAndDateRangeAsync(ClientId, Arg.Any<DateOnly>(), Arg.Any<DateOnly>(), Arg.Any<CancellationToken>())
            .Returns(new List<Break>());

        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>
        {
            ["clientId"] = ClientId.ToString(),
            ["date"] = "2026-08-04"
        });

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("No planned absence");
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task MissingIdentification_ReturnsError()
    {
        var result = await _skill.ExecuteAsync(Context(), new Dictionary<string, object>());

        result.Success.ShouldBeFalse();
        result.Message.ShouldContain("placeholderId");
    }
}
