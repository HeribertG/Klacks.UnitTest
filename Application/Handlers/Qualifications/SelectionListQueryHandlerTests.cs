// Copyright (c) Heribert Gasparoli Private. All rights reserved.

using Klacks.Api.Application.Handlers.Qualifications;
using Klacks.Api.Application.Queries.Qualifications;
using Microsoft.Extensions.Logging;

namespace Klacks.UnitTest.Application.Handlers.Qualifications;

[TestFixture]
public class SelectionListQueryHandlerTests
{
    private IQualificationRepository _repository = null!;
    private IActiveIndustriesProvider _activeIndustriesProvider = null!;
    private SelectionListQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IQualificationRepository>();
        _activeIndustriesProvider = Substitute.For<IActiveIndustriesProvider>();
        _handler = new SelectionListQueryHandler(
            _repository,
            _activeIndustriesProvider,
            Substitute.For<ILogger<SelectionListQueryHandler>>());
    }

    [Test]
    public async Task Handle_NoActiveIndustriesSetting_ReturnsUnfilteredList()
    {
        _activeIndustriesProvider.GetActiveIndustrySlugsAsync().Returns((IReadOnlyCollection<string>?)null);
        _repository.GetAllAsync(Arg.Any<CancellationToken>()).Returns(new List<Qualification>
        {
            new() { Id = Guid.NewGuid(), Name = new MultiLanguage { De = "Zusatzausbildung" }, Industry = string.Empty },
            new() { Id = Guid.NewGuid(), Name = new MultiLanguage { De = "Bewachung" }, Industry = "security" },
        });

        var result = await _handler.Handle(new SelectionListQuery(), CancellationToken.None);

        result.Count().ShouldBe(2);
        await _repository.DidNotReceiveWithAnyArgs().GetSelectableAsync(default!, default);
    }

    [Test]
    public async Task Handle_ActiveIndustriesSet_UsesFilteredRepositoryQuery()
    {
        var slugs = new[] { "healthcare" };
        _activeIndustriesProvider.GetActiveIndustrySlugsAsync().Returns(slugs);
        _repository.GetSelectableAsync(slugs, Arg.Any<CancellationToken>()).Returns(new List<Qualification>
        {
            new() { Id = Guid.NewGuid(), Name = new MultiLanguage { De = "Anästhesiepflege" }, Industry = "healthcare" },
        });

        var result = await _handler.Handle(new SelectionListQuery(), CancellationToken.None);

        result.Single().Name.De.ShouldBe("Anästhesiepflege");
        await _repository.DidNotReceiveWithAnyArgs().GetAllAsync(default);
    }
}
