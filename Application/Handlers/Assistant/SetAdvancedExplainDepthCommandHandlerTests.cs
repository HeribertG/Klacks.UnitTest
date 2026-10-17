// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// The "I already know Klacks" chip: stores "advanced" as the caller's explanation-depth override through the same
/// service the set_explain_depth skill uses (new row first, older rows retired, cache dropped), without an LLM turn.
/// A caller id that is no GUID and a missing agent write nothing.
/// </summary>

using Klacks.Api.Application.Commands.Assistant;
using Klacks.Api.Application.DTOs.Assistant;
using Klacks.Api.Application.Handlers.Assistant;
using Klacks.Api.Application.Interfaces.Assistant;
using Klacks.Api.Application.Services.Assistant;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Handlers.Assistant;

[TestFixture]
public class SetAdvancedExplainDepthCommandHandlerTests
{
    private static readonly Guid CallerId = Guid.NewGuid();
    private static readonly Guid AgentId = Guid.NewGuid();

    private IAgentMemoryRepository _memoryRepository = null!;
    private IAgentRepository _agentRepository = null!;
    private IExplainDepthResolver _resolver = null!;
    private SetAdvancedExplainDepthCommandHandler _handler = null!;
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
        _handler = new SetAdvancedExplainDepthCommandHandler(
            new ExplainDepthOverrideService(_memoryRepository, _agentRepository, _resolver));
    }

    [Test]
    public async Task StoresAdvancedAsTheCallersOverride()
    {
        var outcome = await _handler.Handle(
            new SetAdvancedExplainDepthCommand { UserId = CallerId.ToString() }, CancellationToken.None);

        outcome.ShouldBe(ExplainDepthOverrideOutcome.Stored);
        _stored.ShouldNotBeNull();
        _stored!.UserId.ShouldBe(CallerId);
        _stored.AgentId.ShouldBe(AgentId);
        _stored.Key.ShouldBe(ExplainDepthDefaults.OverrideMemoryKey);
        _stored.Content.ShouldBe(ExplainDepthDefaults.OverrideValueAdvanced);
        Received.InOrder(() =>
        {
            _memoryRepository.AddAsync(Arg.Any<AgentMemory>(), Arg.Any<CancellationToken>());
            _memoryRepository.SoftDeleteByUserAndKeyAsync(
                CallerId, ExplainDepthDefaults.OverrideMemoryKey, Arg.Any<Guid?>(), Arg.Any<CancellationToken>());
            _resolver.Invalidate(CallerId);
        });
    }

    [TestCase("")]
    [TestCase("not-a-guid")]
    public async Task ACallerIdThatIsNoGuid_WritesNothing(string userId)
    {
        var outcome = await _handler.Handle(new SetAdvancedExplainDepthCommand { UserId = userId }, CancellationToken.None);

        outcome.ShouldBe(ExplainDepthOverrideOutcome.NoUser);
        await _memoryRepository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
    }

    [Test]
    public async Task WithoutAConfiguredAgent_WritesNothing()
    {
        _agentRepository.GetDefaultAgentAsync(Arg.Any<CancellationToken>()).Returns((Agent?)null);

        var outcome = await _handler.Handle(
            new SetAdvancedExplainDepthCommand { UserId = CallerId.ToString() }, CancellationToken.None);

        outcome.ShouldBe(ExplainDepthOverrideOutcome.NoAgent);
        await _memoryRepository.DidNotReceiveWithAnyArgs().AddAsync(default!, default);
        _resolver.DidNotReceiveWithAnyArgs().Invalidate(default);
    }
}
