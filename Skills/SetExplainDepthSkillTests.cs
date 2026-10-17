// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// set_explain_depth: beginner and advanced write a new override memory of the caller (never shared, never pinned, no
/// expiry) and only then retire the caller's older override rows, so a crash in between still resolves to the newest
/// choice; auto retires every override row; each successful call drops the caller's cached depth. Unknown values and a
/// caller without a user id write nothing.
/// </summary>

using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Skills;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Skills;

[TestFixture]
public class SetExplainDepthSkillTests
{
    private static readonly Guid CallerId = Guid.NewGuid();
    private static readonly Guid AgentId = Guid.NewGuid();

    private IAgentMemoryRepository _memoryRepository = null!;
    private IAgentRepository _agentRepository = null!;
    private IExplainDepthResolver _resolver = null!;
    private SetExplainDepthSkill _sut = null!;
    private AgentMemory? _stored;

    [SetUp]
    public void SetUp()
    {
        _stored = null;
        _memoryRepository = Substitute.For<IAgentMemoryRepository>();
        _memoryRepository.AddAsync(Arg.Do<AgentMemory>(m => _stored = m), Arg.Any<CancellationToken>());
        _agentRepository = Substitute.For<IAgentRepository>();
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns(new Agent { Id = AgentId });
        _resolver = Substitute.For<IExplainDepthResolver>();
        _sut = new SetExplainDepthSkill(_memoryRepository, _agentRepository, _resolver);
    }

    private static SkillExecutionContext Ctx(Guid? userId = null) => new()
    {
        UserId = userId ?? CallerId,
        TenantId = Guid.NewGuid(),
        UserName = "planner",
        UserPermissions = new List<string>()
    };

    private static Dictionary<string, object> Depth(string value) =>
        new() { [ExplainDepthDefaults.SetSkillParameterName] = value };

    [TestCase(ExplainDepthDefaults.OverrideValueBeginner)]
    [TestCase(ExplainDepthDefaults.OverrideValueAdvanced)]
    public async Task ALevel_WritesThePersonalOverrideMemory(string value)
    {
        var result = await _sut.ExecuteAsync(Ctx(), Depth(value));

        result.Success.ShouldBeTrue();
        _stored.ShouldNotBeNull();
        _stored!.UserId.ShouldBe(CallerId);
        _stored.AgentId.ShouldBe(AgentId);
        _stored.Key.ShouldBe(ExplainDepthDefaults.OverrideMemoryKey);
        _stored.Content.ShouldBe(value);
        _stored.Category.ShouldBe(MemoryCategories.UserPreference);
        _stored.Importance.ShouldBe(ExplainDepthDefaults.OverrideImportance);
        _stored.IsPinned.ShouldBeFalse();
        _stored.ExpiresAt.ShouldBeNull();
    }

    [Test]
    public async Task ALevel_IsNormalisedBeforeItIsStored()
    {
        await _sut.ExecuteAsync(Ctx(), Depth("  Advanced "));

        _stored!.Content.ShouldBe(ExplainDepthDefaults.OverrideValueAdvanced);
    }

    [Test]
    public async Task ALevel_RetiresTheOlderOverrides_OnlyAfterTheNewOneIsStored_AndKeepsTheNewOne()
    {
        await _sut.ExecuteAsync(Ctx(), Depth(ExplainDepthDefaults.OverrideValueAdvanced));

        Received.InOrder(() =>
        {
            _memoryRepository.AddAsync(Arg.Any<AgentMemory>(), Arg.Any<CancellationToken>());
            _memoryRepository.SoftDeleteByUserAndKeyAsync(
                CallerId, ExplainDepthDefaults.OverrideMemoryKey, Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
            _resolver.Invalidate(CallerId);
        });
        await _memoryRepository.Received(1).SoftDeleteByUserAndKeyAsync(
            CallerId, ExplainDepthDefaults.OverrideMemoryKey, _stored!.Id, Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task Auto_RetiresEveryOverride_WithoutWritingOne()
    {
        var result = await _sut.ExecuteAsync(Ctx(), Depth(ExplainDepthDefaults.OverrideValueAuto));

        result.Success.ShouldBeTrue();
        await _memoryRepository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _memoryRepository.Received(1).SoftDeleteByUserAndKeyAsync(
            CallerId, ExplainDepthDefaults.OverrideMemoryKey, null, Arg.Any<CancellationToken>());
        _resolver.Received(1).Invalidate(CallerId);
    }

    [TestCase("expert")]
    [TestCase("")]
    public async Task AnUnknownValue_IsRejected_WithoutAnyWrite(string value)
    {
        var result = await _sut.ExecuteAsync(Ctx(), Depth(value));

        result.Success.ShouldBeFalse();
        await _memoryRepository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _memoryRepository.DidNotReceiveWithAnyArgs().SoftDeleteByUserAndKeyAsync(default, default!, default, default);
        _resolver.DidNotReceiveWithAnyArgs().Invalidate(default);
    }

    [Test]
    public async Task ACallerWithoutAUserId_IsRejected_WithoutAnyWrite()
    {
        var result = await _sut.ExecuteAsync(Ctx(Guid.Empty), Depth(ExplainDepthDefaults.OverrideValueAdvanced));

        result.Success.ShouldBeFalse();
        await _memoryRepository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        await _memoryRepository.DidNotReceiveWithAnyArgs().SoftDeleteByUserAndKeyAsync(default, default!, default, default);
    }

    [Test]
    public async Task WithoutAConfiguredAgent_NothingIsWritten()
    {
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns((Agent?)null);

        var result = await _sut.ExecuteAsync(Ctx(), Depth(ExplainDepthDefaults.OverrideValueBeginner));

        result.Success.ShouldBeFalse();
        await _memoryRepository.DidNotReceiveWithAnyArgs().SoftDeleteByUserAndKeyAsync(default, default!, default, default);
        _resolver.DidNotReceiveWithAnyArgs().Invalidate(default);
    }
}
