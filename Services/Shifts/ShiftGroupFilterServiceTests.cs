using Klacks.Api.Domain.Interfaces;
using Klacks.Api.Domain.Interfaces.Associations;
using Klacks.Api.Domain.Models.Associations;
using Klacks.Api.Domain.Services.ShiftSchedule;
using NSubstitute;

namespace Klacks.UnitTest.Services.Shifts;

[TestFixture]
public class ShiftGroupFilterServiceTests
{
    private const string UserId = "planner-user-id";

    private IGroupVisibilityService _groupVisibility = null!;
    private IUserService _user = null!;
    private ShiftGroupFilterService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _groupVisibility = Substitute.For<IGroupVisibilityService>();
        _user = Substitute.For<IUserService>();
        _user.GetIdString().Returns(UserId);
        _service = new ShiftGroupFilterService(_groupVisibility, _user);
    }

    [Test]
    public async Task ResolveShiftScheduleGroupIds_Admin_WithoutSelection_IsUnrestricted()
    {
        _groupVisibility.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Unrestricted());

        var result = await _service.ResolveShiftScheduleGroupIdsAsync(null);

        result.ShouldBeNull();
    }

    [Test]
    public async Task ResolveShiftScheduleGroupIds_Admin_WithSelection_ReturnsSelectedGroup()
    {
        var selected = Guid.NewGuid();
        _groupVisibility.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Unrestricted());

        var result = await _service.ResolveShiftScheduleGroupIdsAsync(selected);

        result.ShouldBe([selected]);
    }

    [Test]
    public async Task ResolveShiftScheduleGroupIds_NonAdminWithoutVisibleGroup_IsEmptyNotUnrestricted()
    {
        _groupVisibility.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Restricted([], []));

        var result = await _service.ResolveShiftScheduleGroupIdsAsync(null);

        result.ShouldNotBeNull();
        result.ShouldBeEmpty();
    }

    [Test]
    public async Task ResolveShiftScheduleGroupIds_NonAdminWithVisibleRoot_ReturnsVisibleRoots()
    {
        var root = Guid.NewGuid();
        var child = Guid.NewGuid();
        _groupVisibility.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Restricted([root], [root, child]));

        var result = await _service.ResolveShiftScheduleGroupIdsAsync(null);

        result.ShouldBe([root]);
    }

    [Test]
    public async Task ResolveShiftScheduleGroupIds_NonAdmin_SelectedVisibleGroup_ReturnsSelectedGroup()
    {
        var root = Guid.NewGuid();
        var child = Guid.NewGuid();
        _groupVisibility.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Restricted([root], [root, child]));

        var result = await _service.ResolveShiftScheduleGroupIdsAsync(child);

        result.ShouldBe([child]);
    }

    [Test]
    public async Task ResolveShiftScheduleGroupIds_NonAdmin_SelectedHiddenGroup_IsEmpty()
    {
        var root = Guid.NewGuid();
        var hidden = Guid.NewGuid();
        _groupVisibility.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Restricted([root], [root]));

        var result = await _service.ResolveShiftScheduleGroupIdsAsync(hidden);

        result.ShouldNotBeNull();
        result.ShouldBeEmpty();
    }

    [Test]
    public async Task ResolveShiftScheduleGroupIds_NonAdminWithoutVisibleGroup_SelectedGroup_IsEmpty()
    {
        _groupVisibility.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Restricted([], []));

        var result = await _service.ResolveShiftScheduleGroupIdsAsync(Guid.NewGuid());

        result.ShouldNotBeNull();
        result.ShouldBeEmpty();
    }

    [Test]
    public async Task ResolveShiftScheduleGroupIds_NoCaller_IsUnrestricted_WithoutReadingScope()
    {
        _user.GetIdString().Returns((string?)null);

        var result = await _service.ResolveShiftScheduleGroupIdsAsync(null);

        result.ShouldBeNull();
        await _groupVisibility.DidNotReceive().GetVisibilityScopeAsync();
    }

    [Test]
    public async Task ResolveShiftListGroupIds_NonAdminWithVisibleRoot_ReturnsExpandedGroups()
    {
        var root = Guid.NewGuid();
        var child = Guid.NewGuid();
        _groupVisibility.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Restricted([root], [root, child]));

        var result = await _service.ResolveShiftListGroupIdsAsync(null);

        result.ShouldBe([root, child]);
    }

    [Test]
    public async Task ResolveShiftListGroupIds_NonAdminWithoutVisibleGroup_IsEmptyNotUnrestricted()
    {
        _groupVisibility.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Restricted([], []));

        var result = await _service.ResolveShiftListGroupIdsAsync(null);

        result.ShouldNotBeNull();
        result.ShouldBeEmpty();
    }

    [Test]
    public async Task ResolveShiftListGroupIds_NonAdmin_SelectedHiddenGroup_IsEmpty()
    {
        var root = Guid.NewGuid();
        _groupVisibility.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Restricted([root], [root]));

        var result = await _service.ResolveShiftListGroupIdsAsync(Guid.NewGuid());

        result.ShouldNotBeNull();
        result.ShouldBeEmpty();
    }

    [Test]
    public async Task ResolveShiftListGroupIds_Admin_WithoutSelection_IsUnrestricted()
    {
        _groupVisibility.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Unrestricted());

        var result = await _service.ResolveShiftListGroupIdsAsync(null);

        result.ShouldBeNull();
    }

    [Test]
    public async Task GetVisibleGroupIds_NonAdmin_SelectedHiddenGroup_FallsBackToVisibleRoots()
    {
        var root = Guid.NewGuid();
        _groupVisibility.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Restricted([root], [root]));
        _groupVisibility.IsAdmin().Returns(false);
        _groupVisibility.ReadVisibleRootIdList().Returns([root]);

        var result = await _service.GetVisibleGroupIdsAsync(Guid.NewGuid());

        result.ShouldBe([root]);
    }

    [Test]
    public async Task GetVisibleGroupIds_NonAdmin_SelectedVisibleSubgroup_ReturnsSelection()
    {
        var root = Guid.NewGuid();
        var child = Guid.NewGuid();
        _groupVisibility.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Restricted([root], [root, child]));

        var result = await _service.GetVisibleGroupIdsAsync(child);

        result.ShouldBe([child]);
    }

    [Test]
    public async Task GetVisibleGroupIds_Admin_AnySelection_ReturnsSelection()
    {
        var selected = Guid.NewGuid();
        _groupVisibility.GetVisibilityScopeAsync().Returns(GroupVisibilityScope.Unrestricted());

        var result = await _service.GetVisibleGroupIdsAsync(selected);

        result.ShouldBe([selected]);
    }

    [Test]
    public async Task ResolveShiftScheduleGroupIds_NoCaller_WithSelection_ReturnsSelectedGroup()
    {
        var selected = Guid.NewGuid();
        _user.GetIdString().Returns((string?)null);

        var result = await _service.ResolveShiftScheduleGroupIdsAsync(selected);

        result.ShouldBe([selected]);
    }
}
