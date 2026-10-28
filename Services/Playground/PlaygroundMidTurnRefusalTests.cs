// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Tests that a budget refusal reaches the chat pipeline as a plain, non-retried failure: the refusal text is
/// not classified as transient, the stream reader reports Failed (not Cancelled) after exactly one reservation
/// attempt, and the non-streaming retry wrapper returns the failed response without a second attempt.
/// </summary>

using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Interfaces.Playground;
using Klacks.Api.Domain.Models.Playground;
using Klacks.Api.Domain.Services.Assistant.Providers;
using Klacks.Api.Domain.Services.Playground;
using Klacks.Api.Infrastructure.Services.Assistant.Providers.Playground;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using NUnit.Framework;
using Shouldly;

namespace Klacks.UnitTest.Services.Playground;

[TestFixture]
public class PlaygroundMidTurnRefusalTests
{
    private IPlaygroundTokenBudget _budget = null!;
    private ILLMProvider _inner = null!;
    private PlaygroundBudgetedLLMProvider _sut = null!;
    private PlaygroundVisitor _visitor = null!;

    [SetUp]
    public void SetUp()
    {
        _budget = Substitute.For<IPlaygroundTokenBudget>();
        _budget.TryReserve(Arg.Any<PlaygroundVisitor?>(), Arg.Any<long>()).Returns((PlaygroundBudgetReservation?)null);
        _inner = Substitute.For<ILLMProvider>();
        _sut = new PlaygroundBudgetedLLMProvider(_inner, _budget);
        _visitor = new PlaygroundVisitor("203.0.113.1", "device");
        PlaygroundVisitorContext.Set(_visitor);
    }

    [TearDown]
    public void TearDown()
    {
        PlaygroundVisitorContext.Clear();
    }

    [Test]
    public void RefusalText_IsNotClassifiedAsTransient()
    {
        TransientProviderErrorDetector.IsTransient(PlaygroundTokenLimitConstants.LimitReachedMessage).ShouldBeFalse();
        TransientProviderErrorDetector.IsTransient(PlaygroundTokenLimitConstants.ErrorCode).ShouldBeFalse();
    }

    [Test]
    public async Task StreamReader_ReportsTheRefusalAsAFailureWithoutRetry()
    {
        var reader = new ProviderStreamReader(NullLogger.Instance);
        var tokens = new List<string>();

        await foreach (var token in reader.ReadAsync(_sut, new LLMProviderRequest(), "model", CancellationToken.None))
        {
            tokens.Add(token);
        }

        reader.Failed.ShouldBeTrue();
        reader.Cancelled.ShouldBeFalse();
        tokens.ShouldBeEmpty();
        _budget.Received(1).TryReserve(_visitor, Arg.Any<long>());
        _visitor.TokenLimitHit.ShouldBeTrue();
    }

    [Test]
    public async Task TransientRetry_ReturnsTheRefusedResponseAfterASingleAttempt()
    {
        var response = await TransientProviderRetry.ProcessAsync(_sut, new LLMProviderRequest(), NullLogger.Instance);

        response.Success.ShouldBeFalse();
        response.Error.ShouldBe(PlaygroundTokenLimitConstants.LimitReachedMessage);
        _budget.Received(1).TryReserve(_visitor, Arg.Any<long>());
        _visitor.TokenLimitHit.ShouldBeTrue();
    }
}
