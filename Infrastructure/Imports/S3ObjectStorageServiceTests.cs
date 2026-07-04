using Amazon.S3;
using Amazon.S3.Model;
using Klacks.Api.Domain.Services.Imports;
using Klacks.Api.Infrastructure.Services.Imports;
using Microsoft.Extensions.Options;
using NSubstitute;
using Shouldly;

namespace Klacks.UnitTest.Infrastructure.Imports;

[TestFixture]
public class S3ObjectStorageServiceTests
{
    private IAmazonS3 _client = null!;
    private S3ObjectStorageService _service = null!;

    [SetUp]
    public void SetUp()
    {
        _client = Substitute.For<IAmazonS3>();
        var options = Options.Create(new ErpObjectStorageOptions { BucketName = "klacks-erp-import" });
        _service = new S3ObjectStorageService(_client, options);
    }

    [TearDown]
    public void TearDown()
    {
        _client.Dispose();
    }

    [Test]
    public async Task ListAsync_FollowsContinuationToken_UntilNotTruncated()
    {
        var firstPage = new ListObjectsV2Response
        {
            S3Objects = [new S3Object { Key = "customer-1/order-1.xml" }],
            IsTruncated = true,
            NextContinuationToken = "token-2"
        };
        var secondPage = new ListObjectsV2Response
        {
            S3Objects = [new S3Object { Key = "customer-1/order-2.xml" }],
            IsTruncated = false
        };

        _client.ListObjectsV2Async(Arg.Is<ListObjectsV2Request>(r => r.ContinuationToken == null), Arg.Any<CancellationToken>())
            .Returns(firstPage);
        _client.ListObjectsV2Async(Arg.Is<ListObjectsV2Request>(r => r.ContinuationToken == "token-2"), Arg.Any<CancellationToken>())
            .Returns(secondPage);

        var keys = await _service.ListAsync("customer-1/");

        keys.ShouldBe(["customer-1/order-1.xml", "customer-1/order-2.xml"]);
    }

    [Test]
    public async Task UploadAsync_PutsObjectUnderConfiguredBucket()
    {
        using var content = new MemoryStream([1, 2, 3]);

        await _service.UploadAsync("customer-1/order-1.xml", content);

        await _client.Received(1).PutObjectAsync(
            Arg.Is<PutObjectRequest>(r => r.BucketName == "klacks-erp-import" && r.Key == "customer-1/order-1.xml"),
            Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task MoveAsync_CopiesThenDeletesSource()
    {
        await _service.MoveAsync("customer-1/order-1.xml", "customer-1/processed/order-1.xml");

        await _client.Received(1).CopyObjectAsync(
            Arg.Is<CopyObjectRequest>(r =>
                r.SourceBucket == "klacks-erp-import" &&
                r.SourceKey == "customer-1/order-1.xml" &&
                r.DestinationBucket == "klacks-erp-import" &&
                r.DestinationKey == "customer-1/processed/order-1.xml"),
            Arg.Any<CancellationToken>());

        await _client.Received(1).DeleteObjectAsync("klacks-erp-import", "customer-1/order-1.xml", Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task DeleteAsync_DeletesObjectUnderConfiguredBucket()
    {
        await _service.DeleteAsync("customer-1/error/order-1.xml");

        await _client.Received(1).DeleteObjectAsync("klacks-erp-import", "customer-1/error/order-1.xml", Arg.Any<CancellationToken>());
    }
}
