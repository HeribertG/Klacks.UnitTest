using Klacks.Api.Application.Services.Imports;
using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Models.Imports;

namespace Klacks.UnitTest.Application.Services.Imports;

[TestFixture]
public class ErpDefaultDropPointProviderTests
{
    private IErpDropPointRepository _repository = null!;
    private IUnitOfWork _unitOfWork = null!;
    private ErpDefaultDropPointProvider _provider = null!;

    [SetUp]
    public void SetUp()
    {
        _repository = Substitute.For<IErpDropPointRepository>();
        _unitOfWork = Substitute.For<IUnitOfWork>();
        _provider = new ErpDefaultDropPointProvider(_repository, _unitOfWork);
    }

    [Test]
    public async Task GetOrCreateDefaultAsync_DropPointExists_ReturnsItWithoutCreating()
    {
        var existing = new ErpDropPoint
        {
            Id = Guid.NewGuid(),
            Name = "Existing",
            SourceSystemId = "erp-existing",
            BucketPrefix = "erp/existing"
        };
        _repository.List().Returns(new List<ErpDropPoint> { existing });

        var result = await _provider.GetOrCreateDefaultAsync();

        result.ShouldBeSameAs(existing);
        await _repository.DidNotReceive().Add(Arg.Any<ErpDropPoint>());
        await _unitOfWork.DidNotReceive().CompleteAsync();
    }

    [Test]
    public async Task GetOrCreateDefaultAsync_MultipleDropPoints_ReturnsOldestByCreateTime()
    {
        var older = new ErpDropPoint { Id = Guid.NewGuid(), Name = "Older", CreateTime = new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc) };
        var newer = new ErpDropPoint { Id = Guid.NewGuid(), Name = "Newer", CreateTime = new DateTime(2026, 6, 1, 0, 0, 0, DateTimeKind.Utc) };
        _repository.List().Returns(new List<ErpDropPoint> { newer, older });

        var result = await _provider.GetOrCreateDefaultAsync();

        result.ShouldBeSameAs(older);
        await _repository.DidNotReceive().Add(Arg.Any<ErpDropPoint>());
    }

    [Test]
    public async Task GetOrCreateDefaultAsync_NoDropPoint_CreatesOneWithDefaults()
    {
        _repository.List().Returns(new List<ErpDropPoint>());
        ErpDropPoint? added = null;
        await _repository.Add(Arg.Do<ErpDropPoint>(dropPoint => added = dropPoint));
        _repository.ClearReceivedCalls();

        var result = await _provider.GetOrCreateDefaultAsync();

        added.ShouldNotBeNull();
        result.ShouldBeSameAs(added);
        added.Id.ShouldNotBe(Guid.Empty);
        added.Name.ShouldBe(ErpDropPointDefaults.Name);
        added.SourceSystemId.ShouldBe(ErpDropPointDefaults.SourceSystemId);
        added.BucketPrefix.ShouldBe(ErpDropPointDefaults.BucketPrefix);
        added.IsEnabled.ShouldBe(ErpDropPointDefaults.IsEnabled);
        await _unitOfWork.Received(1).CompleteAsync();
    }
}
