// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

using Klacks.Api.Application.Handlers.Settings.Macros;
using Klacks.Api.Application.Queries.Settings.Macros;
using Shouldly;

namespace Klacks.UnitTest.Application.Handlers.Settings.Macros;

[TestFixture]
public class GetDefaultShiftMacroQueryHandlerTests
{
    [Test]
    public async Task Handle_DefaultConfigured_ReturnsTheResolvedMacroId()
    {
        var macroId = Guid.NewGuid();
        var resolver = Substitute.For<IDefaultShiftMacroResolver>();
        resolver.ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>()).Returns(macroId);

        var result = await new GetDefaultShiftMacroQueryHandler(resolver).Handle(new GetDefaultShiftMacroQuery(), CancellationToken.None);

        result.MacroId.ShouldBe(macroId);
    }

    [Test]
    public async Task Handle_NoDefaultConfigured_ReturnsNullMacroId()
    {
        var resolver = Substitute.For<IDefaultShiftMacroResolver>();
        resolver.ResolveDefaultMacroIdAsync(Arg.Any<CancellationToken>()).Returns((Guid?)null);

        var result = await new GetDefaultShiftMacroQueryHandler(resolver).Handle(new GetDefaultShiftMacroQuery(), CancellationToken.None);

        result.MacroId.ShouldBeNull();
    }
}
