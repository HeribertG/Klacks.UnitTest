// Copyright (c) Heribert Gasparoli Private. All rights reserved.

/// <summary>
/// Unit tests for the plugin-language extension of CompletionClaimDetector — verifies that completion
/// claims configured from completion-claim.json are honoured for non-segmented scripts (substring match
/// for Chinese) and multi-word phrases, without substring false positives in segmented languages.
/// </summary>

using Klacks.Api.Domain.Services.Assistant;

namespace Klacks.UnitTest.Domain.Services.Assistant;

[TestFixture]
public class CompletionClaimDetectorPluginTests
{
    [OneTimeSetUp]
    public void ConfigurePluginEntries()
    {
        CompletionClaimDetector.Configure(
            completionClaims:
            [
                "已创建", "已删除", "erfolgreich abgeschlossen",
                "作成しました", "đã tạo", "قمت بإنشاء", "utworzono", "vytvořeno",
            ]);
    }

    [TestCase("已创建")]
    [TestCase("客户已创建")]
    [TestCase("Der Vorgang wurde erfolgreich abgeschlossen")]
    public void ClaimsCompletion_True_For_PluginLanguage_Claims(string response)
    {
        CompletionClaimDetector.ClaimsCompletion(response).ShouldBeTrue(response);
    }

    [TestCase("顧客を作成しました。", TestName = "ja_polite_past_created")]
    [TestCase("客户已创建，请查看列表。", TestName = "zhCN_already_created")]
    [TestCase("Tôi đã tạo khách hàng mới.", TestName = "vi_past_created")]
    [TestCase("لقد قمت بإنشاء العميل بنجاح.", TestName = "ar_first_person_created")]
    [TestCase("Utworzono klienta pomyślnie.", TestName = "pl_impersonal_created")]
    [TestCase("Úspěšně vytvořeno.", TestName = "cs_impersonal_done")]
    public void ClaimsCompletion_True_For_RealisticCompletionSentences(string response)
    {
        CompletionClaimDetector.ClaimsCompletion(response).ShouldBeTrue(response);
    }

    [TestCase("顧客を作成するには、フォームに入力して保存します。", TestName = "ja_howto_present_not_matched")]
    [TestCase("Aby utworzyć klienta, kliknij przycisk Dodaj.", TestName = "pl_howto_infinitive_not_matched")]
    [TestCase("Để tạo khách hàng, hãy nhấn nút Thêm.", TestName = "vi_howto_infinitive_not_matched")]
    [TestCase("لإنشاء عميل، انقر على زر الإضافة.", TestName = "ar_howto_verbal_noun_not_matched")]
    public void ClaimsCompletion_False_For_HowToAnswers(string response)
    {
        CompletionClaimDetector.ClaimsCompletion(response)
            .ShouldBeFalse($"how-to phrasing must not be read as a completion claim: {response}");
    }

    [Test]
    public void PluginPhrase_DoesNotMatch_As_Substring_In_SegmentedLanguage()
    {
        CompletionClaimDetector.ClaimsCompletion("Der Vorgang war erfolgreich, aber noch nicht fertig.")
            .ShouldBeFalse("the whole phrase 'erfolgreich abgeschlossen' must be present, not just 'erfolgreich'");
    }
}
