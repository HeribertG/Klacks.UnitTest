// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Pins that the shifts created by create_test_environment carry the default Shift-category macro, like every
/// other creation path, and stay without macro when no default is configured.
/// </summary>

using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Models.Staffs;
using Klacks.UnitTest.TestHelpers;
using Shouldly;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class CreateTestEnvironmentSkillMacroTests
{
    private const string GroupName = "Bern";

    private IShiftRepository _shiftRepository = null!;
    private IDefaultShiftMacroResolver _defaultShiftMacroResolver = null!;
    private CreateTestEnvironmentSkill _skill = null!;

    [SetUp]
    public void SetUp()
    {
        var clientRepository = Substitute.For<IClientRepository>();
        clientRepository.List().Returns(new List<Client>());
        var groupRepository = Substitute.For<IGroupRepository>();
        groupRepository.List().Returns(new List<Group> { new() { Id = Guid.NewGuid(), Name = GroupName } });
        _shiftRepository = Substitute.For<IShiftRepository>();
        _shiftRepository.AddWithSealedOrderHandling(Arg.Any<Shift>()).Returns(ci => ci.Arg<Shift>());
        _defaultShiftMacroResolver = Substitute.For<IDefaultShiftMacroResolver>();

        _skill = new CreateTestEnvironmentSkill(
            clientRepository,
            Substitute.For<IAddressRepository>(),
            Substitute.For<IContractRepository>(),
            groupRepository,
            Substitute.For<IGroupItemRepository>(),
            _shiftRepository,
            Substitute.For<IUnitOfWork>(),
            new FixedCompanyClock(new DateTimeOffset(2026, 10, 19, 0, 0, 0, TimeSpan.Zero)),
            _defaultShiftMacroResolver);
    }

    private static SkillExecutionContext Ctx() => new()
    {
        UserId = Guid.NewGuid(),
        TenantId = Guid.NewGuid(),
        UserName = "tester",
        UserPermissions = new List<string>()
    };

    private static Dictionary<string, object> Params() => new()
    {
        ["city"] = GroupName,
        ["numberOfEmployees"] = 1,
    };

    [Test]
    public async Task ExecuteAsync_CreatesShiftsWithTheDefaultShiftMacro()
    {
        var defaultMacroId = Guid.NewGuid();
        _defaultShiftMacroResolver.ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>()).Returns(defaultMacroId);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        await _shiftRepository.Received(3).AddWithSealedOrderHandling(Arg.Is<Shift>(s => s.MacroId == defaultMacroId));
    }

    [Test]
    public async Task ExecuteAsync_NoDefaultConfigured_CreatesShiftsWithoutMacro()
    {
        _defaultShiftMacroResolver.ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>()).Returns((Guid?)null);

        var result = await _skill.ExecuteAsync(Ctx(), Params());

        result.Success.ShouldBeTrue(result.Message);
        await _shiftRepository.Received(3).AddWithSealedOrderHandling(Arg.Is<Shift>(s => s.MacroId == null));
    }
}
