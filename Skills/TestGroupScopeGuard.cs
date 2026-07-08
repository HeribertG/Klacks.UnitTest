// Copyright (c) Heribert Gasparoli Private. All rights reserved.

namespace Klacks.UnitTest.Skills;

internal static class TestGroupScopeGuard
{
    public static IGroupScopeGuard Unrestricted()
    {
        var guard = Substitute.For<IGroupScopeGuard>();
        guard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Unrestricted());
        return guard;
    }

    public static IGroupScopeGuard Restricted(IEnumerable<Guid> visibleRootIds, params string[] visibleRootNames)
    {
        var guard = Substitute.For<IGroupScopeGuard>();
        guard.GetAccessAsync(Arg.Any<SkillExecutionContext>(), Arg.Any<CancellationToken>())
            .Returns(GroupScopeAccess.Restricted(visibleRootIds, visibleRootNames));
        return guard;
    }
}
