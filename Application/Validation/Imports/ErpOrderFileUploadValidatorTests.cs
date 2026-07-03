using Klacks.Api.Application.Validation.Imports;
using Klacks.Api.Domain.Constants;

namespace Klacks.UnitTest.Application.Validation.Imports;

[TestFixture]
public class ErpOrderFileUploadValidatorTests
{
    private const long ValidFileLength = 1024;

    [Test]
    public void Validate_ZeroLength_ReturnsEmptyFileError()
    {
        var result = ErpOrderFileUploadValidator.Validate("orders.xml", 0);

        result.ShouldBe(ErpOrderFileUploadValidator.EmptyFileError);
    }

    [Test]
    public void Validate_MissingFileName_ReturnsInvalidExtensionError()
    {
        var result = ErpOrderFileUploadValidator.Validate(null, ValidFileLength);

        result.ShouldBe(ErpOrderFileUploadValidator.InvalidExtensionError);
    }

    [Test]
    public void Validate_WrongExtension_ReturnsInvalidExtensionError()
    {
        var result = ErpOrderFileUploadValidator.Validate("orders.txt", ValidFileLength);

        result.ShouldBe(ErpOrderFileUploadValidator.InvalidExtensionError);
    }

    [Test]
    public void Validate_UppercaseXmlExtension_IsValid()
    {
        var result = ErpOrderFileUploadValidator.Validate("ORDERS.XML", ValidFileLength);

        result.ShouldBeNull();
    }

    [Test]
    public void Validate_FileAboveSizeLimit_ReturnsFileTooLargeError()
    {
        var result = ErpOrderFileUploadValidator.Validate("orders.xml", ErpOrderUploadConstants.MaxFileSizeBytes + 1);

        result.ShouldBe(ErpOrderFileUploadValidator.FileTooLargeError);
    }

    [Test]
    public void Validate_FileAtSizeLimit_IsValid()
    {
        var result = ErpOrderFileUploadValidator.Validate("orders.xml", ErpOrderUploadConstants.MaxFileSizeBytes);

        result.ShouldBeNull();
    }
}
