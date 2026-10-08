// Copyright (c) Heribert Gasparoli. SPDX-License-Identifier: AGPL-3.0-only

/// <summary>
/// Unit tests for the payroll fields GetExportLogQueryHandler adds to ExportLogDto: IsSupplementary and PersonCount are
/// copied, HasArtifact is true exactly when the log carries a storage key, and a log without group stays without
/// group name.
/// </summary>
using Shouldly;
using Klacks.Api.Application.Handlers.PeriodClosing;
using Klacks.Api.Application.Interfaces;
using Klacks.Api.Application.Queries.PeriodClosing;
using Klacks.Api.Domain.Models.Exports;
using Microsoft.Extensions.Logging;
using NSubstitute;

namespace Klacks.UnitTest.Application.Handlers.PeriodClosing;

[TestFixture]
public class GetExportLogQueryHandlerPayrollFieldsTests
{
    private static readonly DateOnly From = new(2026, 1, 1);
    private static readonly DateOnly To = new(2026, 1, 31);

    [Test]
    public async Task Handle_MapsSupplementaryPersonCountAndArtifactAvailability()
    {
        var withArtifact = new ExportLog
        {
            Id = Guid.NewGuid(), Format = "paxml-se", IsSupplementary = true, PersonCount = 4,
            StorageKey = "payroll-export/20260101-20260131/x.xml", ExportedBy = "u1"
        };
        var withoutArtifact = new ExportLog { Id = Guid.NewGuid(), Format = "csv", PersonCount = 0, ExportedBy = "u1" };

        var logRepository = Substitute.For<IExportLogRepository>();
        logRepository.GetRangeAsync(From, To, Arg.Any<CancellationToken>()).Returns([withArtifact, withoutArtifact]);
        var readRepository = Substitute.For<IPeriodClosingReadRepository>();
        readRepository.GetGroupNames(Arg.Any<IReadOnlyCollection<Guid>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<Guid, string>());
        readRepository.GetUserDisplayNames(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new Dictionary<string, string>());
        var handler = new GetExportLogQueryHandler(
            logRepository, readRepository, Substitute.For<ILogger<GetExportLogQueryHandler>>());

        var result = await handler.Handle(new GetExportLogQuery(From, To), CancellationToken.None);

        var first = result.Single(r => r.Id == withArtifact.Id);
        first.IsSupplementary.ShouldBeTrue();
        first.PersonCount.ShouldBe(4);
        first.HasArtifact.ShouldBeTrue();
        first.GroupName.ShouldBeNull();

        var second = result.Single(r => r.Id == withoutArtifact.Id);
        second.IsSupplementary.ShouldBeFalse();
        second.HasArtifact.ShouldBeFalse();
    }
}
