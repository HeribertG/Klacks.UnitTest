using Klacks.Api.Application.Handlers.ErpImportTokens;
using Klacks.Api.Application.Queries.ErpImportTokens;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Imports;

namespace Klacks.UnitTest.Application.Handlers.ErpImportTokens;

[TestFixture]
public class GetErpImportTokensQueryHandlerTests
{
    private IErpImportTokenRepository _repository = null!;
    private GetErpImportTokensQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IErpImportTokenRepository>();
        _handler = new GetErpImportTokensQueryHandler(_repository);
    }

    [Test]
    public async Task Handle_MapsTokensWithoutExposingHash()
    {
        var dropPointId = Guid.NewGuid();
        var token = new ErpImportToken
        {
            Id = Guid.NewGuid(),
            DropPointId = dropPointId,
            Name = "nightly-export",
            TokenHash = "should-never-be-exposed",
            TokenPrefix = "klacks_erp_ab12",
            ExpiresAt = DateTime.UtcNow.AddDays(30)
        };
        _repository.GetByDropPointAsync(dropPointId, Arg.Any<CancellationToken>()).Returns([token]);

        var result = await _handler.Handle(new GetErpImportTokensQuery(dropPointId), CancellationToken.None);

        result.Count.ShouldBe(1);
        result[0].Id.ShouldBe(token.Id);
        result[0].TokenPrefix.ShouldBe("klacks_erp_ab12");
    }
}
