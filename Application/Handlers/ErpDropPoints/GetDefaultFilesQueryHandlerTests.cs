using Klacks.Api.Application.Handlers.ErpDropPoints;
using Klacks.Api.Application.Queries.ErpDropPoints;
using Klacks.Api.Domain.Interfaces.Imports;
using Klacks.Api.Domain.Models.Imports;

namespace Klacks.UnitTest.Application.Handlers.ErpDropPoints;

[TestFixture]
public class GetDefaultFilesQueryHandlerTests
{
    private const string BucketPrefix = "erp/orders";
    private const string Prefix = "erp/orders/";
    private const string UploadId = "0123456789abcdef0123456789abcdef";
    private static readonly DateTime BaseTimeUtc = new(2026, 7, 1, 12, 0, 0, DateTimeKind.Utc);

    private IErpDefaultDropPointProvider _defaultDropPointProvider = null!;
    private IObjectStorageService _objectStorageService = null!;
    private IErpImportExceptionRepository _exceptionRepository = null!;
    private GetDefaultFilesQueryHandler _handler = null!;

    [SetUp]
    public void SetUp()
    {
        _defaultDropPointProvider = Substitute.For<IErpDefaultDropPointProvider>();
        _objectStorageService = Substitute.For<IObjectStorageService>();
        _exceptionRepository = Substitute.For<IErpImportExceptionRepository>();
        _handler = new GetDefaultFilesQueryHandler(_defaultDropPointProvider, _objectStorageService, _exceptionRepository);

        _defaultDropPointProvider.GetOrCreateDefaultAsync(Arg.Any<CancellationToken>())
            .Returns(new ErpDropPoint { Id = Guid.NewGuid(), Name = "Default", SourceSystemId = "default", BucketPrefix = BucketPrefix });
        _exceptionRepository.GetByFileKeysAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>())
            .Returns(new List<ErpImportException>());
    }

    [Test]
    public async Task Handle_GroupsObjectsIntoPendingProcessedAndError()
    {
        _objectStorageService.ListWithMetadataAsync(Prefix, Arg.Any<CancellationToken>()).Returns(new List<StorageObjectMetadata>
        {
            new($"{Prefix}{UploadId}-order-a.xml", 100, BaseTimeUtc.AddHours(3)),
            new($"{Prefix}plain-name.xml", 50, BaseTimeUtc.AddHours(1)),
            new($"{Prefix}processed/{UploadId}-order-b.xml", 70, BaseTimeUtc.AddHours(2)),
            new($"{Prefix}error/{UploadId}-order-c.xml", 30, BaseTimeUtc.AddHours(4))
        });

        var result = await _handler.Handle(new GetDefaultFilesQuery(), CancellationToken.None);

        result.Pending.Select(f => f.Key).ShouldBe([$"{Prefix}{UploadId}-order-a.xml", $"{Prefix}plain-name.xml"]);
        result.Processed.ShouldHaveSingleItem().Key.ShouldBe($"{Prefix}processed/{UploadId}-order-b.xml");
        result.Error.ShouldHaveSingleItem().Key.ShouldBe($"{Prefix}error/{UploadId}-order-c.xml");
    }

    [Test]
    public async Task Handle_DerivesDisplayFileNames_StrippingUploadIdPrefix()
    {
        _objectStorageService.ListWithMetadataAsync(Prefix, Arg.Any<CancellationToken>()).Returns(new List<StorageObjectMetadata>
        {
            new($"{Prefix}{UploadId}-order-a.xml", 100, BaseTimeUtc.AddHours(3)),
            new($"{Prefix}plain-name.xml", 50, BaseTimeUtc.AddHours(1)),
            new($"{Prefix}processed/{UploadId}-order-b.xml", 70, BaseTimeUtc.AddHours(2)),
            new($"{Prefix}error/{UploadId}-order-c.xml", 30, BaseTimeUtc.AddHours(4))
        });

        var result = await _handler.Handle(new GetDefaultFilesQuery(), CancellationToken.None);

        result.Pending.Select(f => f.FileName).ShouldBe(["order-a.xml", "plain-name.xml"]);
        result.Processed.ShouldHaveSingleItem().FileName.ShouldBe("order-b.xml");
        result.Error.ShouldHaveSingleItem().FileName.ShouldBe("order-c.xml");
    }

    [Test]
    public async Task Handle_SortsEachGroupNewestFirst()
    {
        _objectStorageService.ListWithMetadataAsync(Prefix, Arg.Any<CancellationToken>()).Returns(new List<StorageObjectMetadata>
        {
            new($"{Prefix}older.xml", 10, BaseTimeUtc.AddHours(1)),
            new($"{Prefix}newest.xml", 10, BaseTimeUtc.AddHours(5)),
            new($"{Prefix}middle.xml", 10, BaseTimeUtc.AddHours(3))
        });

        var result = await _handler.Handle(new GetDefaultFilesQuery(), CancellationToken.None);

        result.Pending.Select(f => f.FileName).ShouldBe(["newest.xml", "middle.xml", "older.xml"]);
    }

    [Test]
    public async Task Handle_ErrorEntry_CarriesLatestExceptionReasonMatchedByOriginalFileKey()
    {
        var storedFileName = $"{UploadId}-order-c.xml";
        var originalKey = Prefix + storedFileName;
        _objectStorageService.ListWithMetadataAsync(Prefix, Arg.Any<CancellationToken>()).Returns(new List<StorageObjectMetadata>
        {
            new($"{Prefix}error/{storedFileName}", 30, BaseTimeUtc)
        });

        IReadOnlyCollection<string>? requestedKeys = null;
        _exceptionRepository.GetByFileKeysAsync(
                Arg.Do<IReadOnlyCollection<string>>(keys => requestedKeys = keys),
                Arg.Any<CancellationToken>())
            .Returns(new List<ErpImportException>
            {
                new() { FileKey = originalKey, Reason = "older reason", CreateTime = BaseTimeUtc.AddHours(-2) },
                new() { FileKey = originalKey, Reason = "latest reason", CreateTime = BaseTimeUtc.AddHours(-1) }
            });

        var result = await _handler.Handle(new GetDefaultFilesQuery(), CancellationToken.None);

        requestedKeys.ShouldNotBeNull();
        requestedKeys.ShouldBe([originalKey]);
        var errorEntry = result.Error.ShouldHaveSingleItem();
        errorEntry.ErrorReason.ShouldBe("latest reason");
        errorEntry.SizeBytes.ShouldBe(30);
        errorEntry.LastModifiedUtc.ShouldBe(BaseTimeUtc);
    }

    [Test]
    public async Task Handle_ErrorEntryWithoutMatchingException_HasNullErrorReason()
    {
        _objectStorageService.ListWithMetadataAsync(Prefix, Arg.Any<CancellationToken>()).Returns(new List<StorageObjectMetadata>
        {
            new($"{Prefix}error/unmatched.xml", 30, BaseTimeUtc)
        });

        var result = await _handler.Handle(new GetDefaultFilesQuery(), CancellationToken.None);

        result.Error.ShouldHaveSingleItem().ErrorReason.ShouldBeNull();
    }

    [Test]
    public async Task Handle_NoErrorObjects_DoesNotQueryExceptions()
    {
        _objectStorageService.ListWithMetadataAsync(Prefix, Arg.Any<CancellationToken>()).Returns(new List<StorageObjectMetadata>
        {
            new($"{Prefix}pending.xml", 10, BaseTimeUtc)
        });

        await _handler.Handle(new GetDefaultFilesQuery(), CancellationToken.None);

        await _exceptionRepository.DidNotReceive()
            .GetByFileKeysAsync(Arg.Any<IReadOnlyCollection<string>>(), Arg.Any<CancellationToken>());
    }
}
