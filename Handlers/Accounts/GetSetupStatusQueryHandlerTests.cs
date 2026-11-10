// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Handlers.Accounts;
using Klacks.Api.Application.Queries.Accounts;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Options;

namespace Klacks.UnitTest.Handlers.Accounts;

[TestFixture]
public class GetSetupStatusQueryHandlerTests
{
    [TestCase(true)]
    [TestCase(false)]
    public async Task Handle_PublishesTheGateStateAndTheEffectivePasswordPolicy(bool gateActive)
    {
        var gate = Substitute.For<IAdminSetupGateService>();
        gate.IsGateActiveAsync().Returns(gateActive);
        var options = new IdentityOptions();
        options.Password.RequiredLength = 12;
        options.Password.RequireDigit = true;
        options.Password.RequireNonAlphanumeric = false;
        options.Password.RequireUppercase = true;
        options.Password.RequireLowercase = false;
        var sut = new GetSetupStatusQueryHandler(gate, Options.Create(options));

        var status = await sut.Handle(new GetSetupStatusQuery(), CancellationToken.None);

        status.RequiresOwnAdmin.ShouldBe(gateActive);
        status.PasswordPolicy.ShouldNotBeNull();
        status.PasswordPolicy.RequiredLength.ShouldBe(12);
        status.PasswordPolicy.RequireDigit.ShouldBeTrue();
        status.PasswordPolicy.RequireNonAlphanumeric.ShouldBeFalse();
        status.PasswordPolicy.RequireUppercase.ShouldBeTrue();
        status.PasswordPolicy.RequireLowercase.ShouldBeFalse();
        await gate.DidNotReceive().IsFirstStartSetupOpenAsync();
    }

    [TestCase(true, false)]
    [TestCase(false, true)]
    public async Task Handle_FirstStartWhileOpen_AnswersTrueWithThePasswordPolicy(bool isLocalRequest, bool requiresSetupCode)
    {
        var gate = Substitute.For<IAdminSetupGateService>();
        gate.IsFirstStartReachable(isLocalRequest).Returns(true);
        gate.IsFirstStartSetupOpenAsync().Returns(true);
        var sut = new GetSetupStatusQueryHandler(gate, Options.Create(new IdentityOptions()));

        var status = await sut.Handle(
            new GetSetupStatusQuery(IsFirstStart: true, IsLocalRequest: isLocalRequest), CancellationToken.None);

        status.RequiresOwnAdmin.ShouldBeTrue();
        status.RequiresSetupCode.ShouldBe(requiresSetupCode);
        status.PasswordPolicy.ShouldNotBeNull();
        await gate.DidNotReceive().IsGateActiveAsync();
    }

    [Test]
    public async Task Handle_RemoteFirstStartWithoutConfiguredCode_AnswersOnlyFalse()
    {
        var gate = Substitute.For<IAdminSetupGateService>();
        gate.IsFirstStartReachable(false).Returns(false);
        gate.IsFirstStartSetupOpenAsync().Returns(true);
        var sut = new GetSetupStatusQueryHandler(gate, Options.Create(new IdentityOptions()));

        var status = await sut.Handle(new GetSetupStatusQuery(IsFirstStart: true), CancellationToken.None);

        status.RequiresOwnAdmin.ShouldBeFalse();
        status.RequiresSetupCode.ShouldBeFalse();
        status.PasswordPolicy.ShouldBeNull();
    }

    [Test]
    public async Task Handle_FirstStartWhenClosed_AnswersOnlyFalse()
    {
        var gate = Substitute.For<IAdminSetupGateService>();
        gate.IsFirstStartReachable(Arg.Any<bool>()).Returns(true);
        gate.IsFirstStartSetupOpenAsync().Returns(false);
        gate.IsGateActiveAsync().Returns(true);
        var sut = new GetSetupStatusQueryHandler(gate, Options.Create(new IdentityOptions()));

        var status = await sut.Handle(new GetSetupStatusQuery(IsFirstStart: true), CancellationToken.None);

        status.RequiresOwnAdmin.ShouldBeFalse();
        status.RequiresSetupCode.ShouldBeFalse();
        status.PasswordPolicy.ShouldBeNull();
    }
}
