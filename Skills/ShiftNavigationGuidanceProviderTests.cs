// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Services.Assistant.Guidance;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class ShiftNavigationGuidanceProviderTests
{
    private IShiftRepository _shiftRepository = null!;
    private ShiftNavigationGuidanceProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _shiftRepository = Substitute.For<IShiftRepository>();
        _provider = new ShiftNavigationGuidanceProvider(_shiftRepository);
    }

    [TestCase("edit-shift", true)]
    [TestCase("EDIT-SHIFT", true)]
    [TestCase("edit-group", false)]
    [TestCase("cut-shift", false)]
    [TestCase("dashboard", false)]
    public void CanHandle_OnlyEditShift_CaseInsensitive(string pageKey, bool expected)
    {
        Assert.That(_provider.CanHandle(pageKey), Is.EqualTo(expected));
    }

    [TestCase(ShiftStatus.OriginalShift)]
    [TestCase(ShiftStatus.SplitShift)]
    public async Task GetGuidanceAsync_SealedCloneStatus_ReturnsLockNote(ShiftStatus status)
    {
        var id = Guid.NewGuid();
        _shiftRepository.GetNoTracking(id).Returns(new Shift { Id = id, Status = status });

        var guidance = await _provider.GetGuidanceAsync(UiPageKeys.EditShift, id);

        Assert.That(guidance, Does.Contain("explain_shift_lifecycle_order_to_shift"));
        Assert.That(guidance, Does.Contain(status.ToString()));
        Assert.That(guidance, Does.Contain("You MUST proactively tell the user"));
        Assert.That(guidance, Does.Contain("Do not mention internal identifiers"));
    }

    [TestCase(ShiftStatus.OriginalOrder)]
    [TestCase(ShiftStatus.SealedOrder)]
    public async Task GetGuidanceAsync_OrderStatus_ReturnsNull(ShiftStatus status)
    {
        var id = Guid.NewGuid();
        _shiftRepository.GetNoTracking(id).Returns(new Shift { Id = id, Status = status });

        var guidance = await _provider.GetGuidanceAsync(UiPageKeys.EditShift, id);

        Assert.That(guidance, Is.Null);
    }

    [Test]
    public async Task GetGuidanceAsync_UnknownShift_ReturnsNull()
    {
        var id = Guid.NewGuid();
        _shiftRepository.GetNoTracking(id).Returns((Shift?)null);

        var guidance = await _provider.GetGuidanceAsync(UiPageKeys.EditShift, id);

        Assert.That(guidance, Is.Null);
    }
}
