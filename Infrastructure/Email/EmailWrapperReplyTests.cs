// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for EmailWrapper.BuildReplyMessage: sender is the configured reply-to mailbox (with the
/// configured display mark), exactly one recipient, plain-text body, and the given threading headers are
/// attached (empty values skipped). Without a configured reply-to address no message is built; a subject
/// or header value containing CR/LF is rejected so no header can be injected.
/// </summary>

using Klacks.Api.Infrastructure.Email;

namespace Klacks.UnitTest.Infrastructure.Email;

[TestFixture]
public class EmailWrapperReplyTests
{
    private static EmailWrapper Wrapper(string replyTo = "klacks@example.com") => new()
    {
        ReplyTo = replyTo,
        Mark = "Klacks"
    };

    [Test]
    public void BuildReplyMessage_SetsSenderRecipientSubjectPlainBodyAndHeaders()
    {
        var headers = new Dictionary<string, string>
        {
            ["Auto-Submitted"] = "auto-replied",
            ["In-Reply-To"] = "<original-1@example.com>",
            ["References"] = "<older-1@example.com> <original-1@example.com>"
        };

        using var message = Wrapper().BuildReplyMessage("anna@example.com", "Re: Krank", "Kannst du heute nicht arbeiten?", headers);

        message.ShouldNotBeNull();
        message.From!.Address.ShouldBe("klacks@example.com");
        message.To.Single().Address.ShouldBe("anna@example.com");
        message.Subject.ShouldBe("Re: Krank");
        message.IsBodyHtml.ShouldBeFalse();
        message.Body.ShouldStartWith("Kannst du heute nicht arbeiten?");
        message.Headers["Auto-Submitted"].ShouldBe("auto-replied");
        message.Headers["In-Reply-To"].ShouldBe("<original-1@example.com>");
        message.Headers["References"].ShouldBe("<older-1@example.com> <original-1@example.com>");
    }

    [Test]
    public void BuildReplyMessage_SkipsEmptyHeaderValues()
    {
        var headers = new Dictionary<string, string> { ["In-Reply-To"] = " " };

        using var message = Wrapper().BuildReplyMessage("anna@example.com", "Re:", "Frage?", headers);

        message!.Headers["In-Reply-To"].ShouldBeNull();
    }

    [Test]
    public void BuildReplyMessage_WithoutReplyToSetting_ReturnsNull()
    {
        Wrapper(string.Empty).BuildReplyMessage("anna@example.com", "Re:", "Frage?", new Dictionary<string, string>()).ShouldBeNull();
    }

    [Test]
    public void BuildReplyMessage_RecipientListWithSemicolon_IsNotFannedOut()
    {
        Should.Throw<FormatException>(() =>
            Wrapper().BuildReplyMessage("anna@example.com;evil@example.org", "Re:", "Frage?", new Dictionary<string, string>()));
    }

    [TestCase("anna@example.com, evil@example.org")]
    [TestCase("anna@example.com evil@example.org")]
    public void BuildReplyMessage_RecipientListWithCommaOrSpace_IsRejected(string strTo)
    {
        Should.Throw<FormatException>(() =>
            Wrapper().BuildReplyMessage(strTo, "Re:", "Frage?", new Dictionary<string, string>()));
    }

    [TestCase("\r\nBcc: evil@example.org")]
    [TestCase("\nBcc: evil@example.org")]
    [TestCase("\rBcc: evil@example.org")]
    public void BuildReplyMessage_HeaderValueWithLineBreak_IsRejected(string injection)
    {
        var headers = new Dictionary<string, string> { ["In-Reply-To"] = "<original-1@example.com>" + injection };

        Should.Throw<ArgumentException>(() => Wrapper().BuildReplyMessage("anna@example.com", "Re:", "Frage?", headers));
    }

    [Test]
    public void BuildReplyMessage_RecipientWithLineBreak_IsRejected()
    {
        Should.Throw<ArgumentException>(() =>
            Wrapper().BuildReplyMessage("anna@example.com\r\nBcc: evil@example.org", "Re:", "Frage?", new Dictionary<string, string>()));
    }

    [Test]
    public void BuildReplyMessage_SubjectWithLineBreak_IsRejected()
    {
        Should.Throw<ArgumentException>(() =>
            Wrapper().BuildReplyMessage("anna@example.com", "Re: Krank\r\nBcc: evil@example.org", "Frage?", new Dictionary<string, string>()));
    }
}
