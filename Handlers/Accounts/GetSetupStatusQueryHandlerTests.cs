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
        status.PasswordPolicy.RequiredLength.ShouldBe(12);
        status.PasswordPolicy.RequireDigit.ShouldBeTrue();
        status.PasswordPolicy.RequireNonAlphanumeric.ShouldBeFalse();
        status.PasswordPolicy.RequireUppercase.ShouldBeTrue();
        status.PasswordPolicy.RequireLowercase.ShouldBeFalse();
    }
}
