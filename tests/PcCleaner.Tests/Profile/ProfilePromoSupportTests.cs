using System.IO;
using System.Text;
using PcCleaner.Core;
using PcCleaner.Modules.Profile;
using PcCleaner.Tests.Account;
using Xunit;

namespace PcCleaner.Tests.Profile;

public class ProfilePromoSupportTests
{
    public ProfilePromoSupportTests() => TestEnvironment.Init();

    private static ProfileViewModel Create(TestAccount a) => new(a.Service, _ => false, a.Open, (_, _) => true, () => a.Now);

    [Fact]
    public async Task AGoodCode_TurnsProOn_AtOnce()
    {
        using var a = new TestAccount(TestLicense.Json(plan: "free", noExpiry: true));
        a.Fake.On("POST /promo/apply", 200, a.Key.TicketJson(TestLicense.Json(plan: "pro", kind: "promo")));
        var vm = Create(a);
        vm.PromoCode = " WELCOME ";

        await vm.ApplyPromoForTestAsync();

        Assert.Equal("Pro", vm.PlanText);
        Assert.StartsWith("Промокод применён", vm.PromoText);
        Assert.Equal("", vm.PromoCode);
        Assert.Equal("WELCOME", Api.Body(a.Fake.Requests.Single()).GetProperty("code").GetString());
    }

    [Fact]
    public async Task ACodeWhoseTicketWasNotTaken_DoesNotClaimPro()
    {
        using var a = new TestAccount(TestLicense.Json(plan: "free", noExpiry: true));
        // The server took the code, but the ticket it answered with belongs to another computer: the tier stays as it was.
        a.Fake.On("POST /promo/apply", 200, a.Key.TicketJson(TestLicense.Json(plan: "pro", kind: "promo", deviceId: TestLicense.DeviceId + 1)));
        var vm = Create(a);
        vm.PromoCode = "WELCOME";

        await vm.ApplyPromoForTestAsync();

        Assert.Equal("Free", vm.PlanText);
        Assert.StartsWith("Промокод применён.", vm.PromoText);
        Assert.Contains("обновится", vm.PromoText);
        Assert.DoesNotContain("Pro", vm.PromoText);
        Assert.Equal("", vm.PromoCode);
    }

    [Fact]
    public async Task AnEmptyCode_AsksNothing()
    {
        using var a = new TestAccount(TestLicense.Json(plan: "free", noExpiry: true));
        var vm = Create(a);
        vm.PromoCode = "   ";

        await vm.ApplyPromoForTestAsync();

        Assert.Empty(a.Fake.Requests);
        Assert.Null(vm.PromoText);
    }

    [Theory]
    [InlineData(1, "Такого промокода нет")]
    [InlineData(2, "Срок промокода истёк")]
    [InlineData(3, "Промокод уже использован максимальное число раз")]
    [InlineData(4, "Вы уже использовали этот промокод")]
    [InlineData(5, "На этом компьютере")]
    [InlineData(6, "Сначала войдите")]
    public async Task AWrongCode_SaysWhy(int reason, string text)
    {
        using var a = new TestAccount(TestLicense.Json(plan: "free", noExpiry: true));
        a.Fake.OnError("POST /promo/apply", 422, "PROMO_INVALID", $",\"reason\":{reason}");
        var vm = Create(a);
        vm.PromoCode = "X";

        await vm.ApplyPromoForTestAsync();

        Assert.StartsWith(text, vm.PromoText);
        Assert.False(vm.PromoIsDiscount);
        Assert.Equal("X", vm.PromoCode); // a refused code stays for the user to fix
    }

    [Fact]
    public async Task ADiscountCode_PointsToThePurchase()
    {
        using var a = new TestAccount(TestLicense.Json(plan: "free", noExpiry: true));
        a.Fake.OnError("POST /promo/apply", 422, "PROMO_INVALID", ",\"reason\":7");
        var vm = Create(a);
        vm.PromoCode = "SALE10";

        await vm.ApplyPromoForTestAsync();

        Assert.True(vm.PromoIsDiscount);
        Assert.StartsWith("Это код скидки", vm.PromoText);
        vm.OpenBuyWithPromoCommand.Execute(null);
        Assert.Equal(new[] { ProfileViewModel.BuyUrl }, a.Opened);
    }

    [Fact]
    public async Task TooManyTries_SayTryInAnHour()
    {
        using var a = new TestAccount(TestLicense.Json(plan: "free", noExpiry: true));
        a.Fake.OnError("POST /promo/apply", 429, "RATE_LIMITED");
        var vm = Create(a);
        vm.PromoCode = "X";

        await vm.ApplyPromoForTestAsync();

        Assert.Equal("Слишком много попыток. Попробуйте через час.", vm.PromoText);
    }

    [Fact]
    public async Task WhatWasSaidAboutACode_DoesNotOutliveTheSignIn()
    {
        using var a = new TestAccount(TestLicense.Json(plan: "free", noExpiry: true));
        a.Fake.OnError("POST /promo/apply", 422, "PROMO_INVALID", ",\"reason\":7").On("POST /auth/logout", 200, "{\"ok\":true}");
        var vm = Create(a);
        vm.PromoCode = "SALE10";
        await vm.ApplyPromoForTestAsync();
        Assert.True(vm.PromoIsDiscount);

        await vm.LogoutForTestAsync();

        Assert.Null(vm.PromoText);
        Assert.False(vm.PromoIsDiscount);
    }

    [Fact]
    public void AGuest_CannotApplyACode()
    {
        using var a = new TestAccount();

        Assert.False(Create(a).CanApplyPromo);
    }

    [Fact]
    public async Task AGuest_AsksNothingOfTheServer_WhenApplying()
    {
        using var a = new TestAccount();
        var vm = Create(a);
        vm.PromoCode = "WELCOME";

        await vm.ApplyPromoForTestAsync();

        Assert.Empty(a.Fake.Requests);
    }

    [Fact]
    public async Task Support_FromASignedInUser_GoesWithTheAccount_AndTheLog()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.On("POST /ticket", 200, "{\"id\":3}");
        var vm = Create(a);
        vm.SupportTopic = "bug";
        vm.SupportText = "Не открывается раздел Сеть после обновления";
        vm.SupportAttach = true;

        await vm.SendSupportForTestAsync();

        var body = Api.Body(a.Fake.Requests.Single());
        Assert.Equal("user@example.com", body.GetProperty("email").GetString());
        Assert.Equal("bug", body.GetProperty("topic").GetString());
        Assert.True(body.TryGetProperty("log", out var log) && log.GetString()!.Length > 0);
        Assert.Contains("почту", vm.SupportResult);
        Assert.Equal("", vm.SupportText);
    }

    [Fact]
    public async Task Support_WithoutTheLog_SendsNone()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.On("POST /ticket", 200, "{\"id\":3}");
        var vm = Create(a);
        vm.SupportText = "Подробное описание вопроса";
        vm.SupportAttach = false;

        await vm.SendSupportForTestAsync();

        Assert.False(Api.Body(a.Fake.Requests.Single()).TryGetProperty("log", out var log) && log.ValueKind == System.Text.Json.JsonValueKind.String);
    }

    [Fact]
    public async Task Support_AsAGuest_NeedsAnEmail_AndATextOfTenCharacters()
    {
        using var a = new TestAccount();
        a.Fake.On("POST /ticket", 200, "{\"id\":3}");
        var vm = Create(a);
        Assert.True(vm.NeedsSupportEmail);
        vm.SupportText = "Коротко";

        await vm.SendSupportForTestAsync();
        Assert.Empty(a.Fake.Requests);

        vm.SupportText = "Подробное описание вопроса";
        await vm.SendSupportForTestAsync();
        Assert.Empty(a.Fake.Requests);
        Assert.StartsWith("Укажите почту", vm.SupportResult);

        vm.SupportEmail = "guest@example.com";
        await vm.SendSupportForTestAsync();

        Assert.Null(a.Fake.Requests.Single().Bearer);
    }

    [Fact]
    public async Task Support_TooManyMessages_SayTryInAnHour()
    {
        using var a = new TestAccount(TestLicense.Json());
        a.Fake.OnError("POST /ticket", 429, "RATE_LIMITED");
        var vm = Create(a);
        vm.SupportText = "Подробное описание вопроса";

        await vm.SendSupportForTestAsync();

        Assert.Equal("Слишком много обращений. Попробуйте через час.", vm.SupportResult);
        Assert.Equal("Подробное описание вопроса", vm.SupportText); // the text stays to be sent again
    }

    [Fact]
    public void TheAttachment_IsTheEndOfTheNewestLog_InBase64_WithinTheLimit()
    {
        var logs = TestEnvironment.NewTempDir("logs");
        File.WriteAllText(Path.Combine(logs, "2026-11-01.log"), new string('x', 5000) + "END-OF-LOG");

        var text = Encoding.UTF8.GetString(Convert.FromBase64String(SupportAttachment.Build(1000, logs)));

        Assert.True(Encoding.UTF8.GetByteCount(text) <= 1000);
        Assert.EndsWith("END-OF-LOG", text);
        Assert.Contains("WaterCa7s PC Cleaner", text);
    }

    [Fact]
    public void TheAttachment_TakesTheNewestLog_AndKeepsWholeCharacters()
    {
        var logs = TestEnvironment.NewTempDir("logs");
        var old = Path.Combine(logs, "2026-10-01.log");
        File.WriteAllText(old, "OLD-LOG");
        File.SetLastWriteTimeUtc(old, DateTime.UtcNow.AddDays(-5));
        // Two-byte letters: a cut in the middle of one must not leave a broken character at the start of the tail.
        File.WriteAllText(Path.Combine(logs, "2026-11-01.log"), new string('ж', 2000) + "КОНЕЦ", new UTF8Encoding(true));

        var text = Encoding.UTF8.GetString(Convert.FromBase64String(SupportAttachment.Build(1001, logs)));

        Assert.DoesNotContain("OLD-LOG", text);
        Assert.DoesNotContain('�', text);
        Assert.DoesNotContain('﻿', text);
        Assert.EndsWith("КОНЕЦ", text);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= 1001);
    }

    [Fact]
    public void TheAttachment_WithoutALog_IsTheSystemLineOnly_AndNamesNobody()
    {
        var text = Encoding.UTF8.GetString(Convert.FromBase64String(SupportAttachment.Build(1000, Path.Combine(TestEnvironment.DataDir, "no-such-logs"))));

        Assert.StartsWith("WaterCa7s PC Cleaner ", text);
        Assert.Single(text.TrimEnd('\n').Split('\n'));
        Assert.DoesNotContain('@', text);
    }

    [Fact]
    public void TheAttachment_KeepsTheSystemLine_WhenTheLimitIsTight()
    {
        var logs = TestEnvironment.NewTempDir("logs");
        File.WriteAllText(Path.Combine(logs, "2026-11-01.log"), new string('x', 5000));

        var text = Encoding.UTF8.GetString(Convert.FromBase64String(SupportAttachment.Build(200, logs)));

        Assert.StartsWith("WaterCa7s PC Cleaner ", text);
        Assert.True(Encoding.UTF8.GetByteCount(text) <= 200);
    }
}
