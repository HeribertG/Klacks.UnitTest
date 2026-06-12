using Klacks.Api.Domain.Constants;
using Klacks.Api.Domain.Security;

namespace Klacks.UnitTest.Authentification;

[TestFixture]
public class PatTokenGeneratorTests
{
    private const int Sha256HexLength = 64;
    private const int Base64UrlEncodedRandomPartLength = 43;
    private const string Sha256LowercaseHexPattern = "^[0-9a-f]{64}$";
    private const string Base64UrlCharsetPattern = "^[A-Za-z0-9_-]+$";

    [Test]
    public void Generate_Plaintext_StartsWithTokenPrefix()
    {
        var (plaintext, _, _) = PatTokenGenerator.Generate();

        plaintext.ShouldStartWith(PatConstants.TokenPrefix);
    }

    [Test]
    public void Generate_Plaintext_HasExpectedLength()
    {
        var (plaintext, _, _) = PatTokenGenerator.Generate();

        plaintext.Length.ShouldBe(PatConstants.TokenPrefix.Length + Base64UrlEncodedRandomPartLength);
    }

    [Test]
    public void Generate_PlaintextRandomPart_UsesBase64UrlCharset()
    {
        var (plaintext, _, _) = PatTokenGenerator.Generate();

        var randomPart = plaintext[PatConstants.TokenPrefix.Length..];
        randomPart.ShouldMatch(Base64UrlCharsetPattern);
    }

    [Test]
    public void Generate_TokenPrefix_IsFirstTwelveCharactersOfPlaintext()
    {
        var (plaintext, _, tokenPrefix) = PatTokenGenerator.Generate();

        tokenPrefix.Length.ShouldBe(PatConstants.DisplayPrefixLength);
        tokenPrefix.ShouldBe(plaintext[..PatConstants.DisplayPrefixLength]);
    }

    [Test]
    public void Generate_TokenHash_IsLowercaseSha256HexOfPlaintext()
    {
        var (plaintext, tokenHash, _) = PatTokenGenerator.Generate();

        tokenHash.Length.ShouldBe(Sha256HexLength);
        tokenHash.ShouldMatch(Sha256LowercaseHexPattern);
        tokenHash.ShouldBe(PatTokenGenerator.HashToken(plaintext));
    }

    [Test]
    public void HashToken_SameInput_ReturnsSameHash()
    {
        var (plaintext, _, _) = PatTokenGenerator.Generate();

        var firstHash = PatTokenGenerator.HashToken(plaintext);
        var secondHash = PatTokenGenerator.HashToken(plaintext);

        secondHash.ShouldBe(firstHash);
    }

    [Test]
    public void HashToken_DifferentInputs_ReturnDifferentHashes()
    {
        var firstHash = PatTokenGenerator.HashToken("first-token");
        var secondHash = PatTokenGenerator.HashToken("second-token");

        secondHash.ShouldNotBe(firstHash);
    }

    [Test]
    public void Generate_CalledTwice_ReturnsDifferentTokens()
    {
        var (firstPlaintext, firstHash, _) = PatTokenGenerator.Generate();
        var (secondPlaintext, secondHash, _) = PatTokenGenerator.Generate();

        secondPlaintext.ShouldNotBe(firstPlaintext);
        secondHash.ShouldNotBe(firstHash);
    }
}
