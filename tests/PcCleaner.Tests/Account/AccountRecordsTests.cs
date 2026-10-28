using System.Text.Json;
using PcCleaner.Core.Account;
using PcCleaner.Core.Localization;
using Xunit;

namespace PcCleaner.Tests.Account;

/// <summary>The records that carry secrets print them only as set / unset; the texts of an error for the user.</summary>
public class AccountRecordsTests
{
    private const string Secret = "s3cr3t-value-0123456789";

    [Fact]
    public void ALoginResult_HidesTheToken_AndTheTicket()
    {
        var text = new LoginResult(Secret, new AccountInfo("user@example.com", true), new DeviceRef(42, "PC"),
            new SignedTicket(Secret + "t", Secret + "s")).ToString();

        Assert.DoesNotContain(Secret, text);
        Assert.Contains("user@example.com", text);
        Assert.Contains("<set>", text);
    }

    [Fact]
    public void ASignedTicket_HidesTheTicketAndTheSignature()
        => Assert.DoesNotContain(Secret, new SignedTicket(Secret + "t", Secret + "s").ToString());

    [Fact]
    public void TheDeviceFields_HideTheHwHash()
    {
        var text = new DeviceFields(Secret, "windows", "PC", "1.5.0").ToString();

        Assert.DoesNotContain(Secret, text);
        Assert.Contains("windows", text);
    }

    [Fact]
    public void ADeviceLimit_HidesTheUnbindPermit()
        => Assert.DoesNotContain(Secret, new DeviceLimitInfo(Array.Empty<DeviceEntry>(), 1, Secret).ToString());

    [Fact]
    public void AnApiError_PrintsThePayloadOnlyAsJson()
    {
        using var doc = JsonDocument.Parse($"{{\"error\":\"DEVICE_LIMIT\",\"message\":\"m\",\"unbind_token\":\"{Secret}\"}}");
        var text = new ApiError(ApiError.DeviceLimit, "m", 409, doc.RootElement.Clone()).ToString();

        Assert.DoesNotContain(Secret, text);
        Assert.Contains("<json>", text);
        Assert.Contains(ApiError.DeviceLimit, text);
    }

    [Fact]
    public void TheUserText_IsTheServersMessage_WhenThereIsOne()
        => Assert.Equal("Неверный пароль", new ApiError(ApiError.AuthFailed, "Неверный пароль", 401, null).UserText);

    [Fact]
    public void TheUserText_OfTheProgramsOwnErrors_IsNeverEmpty()
    {
        Assert.Equal(T._("Нет связи с сервером. Проверьте интернет и попробуйте ещё раз."), ApiError.NetworkError().UserText);
        Assert.Equal(T._("Сервер не ответил. Попробуйте позже."), new ApiError(ApiError.ServerError, "", 502, null).UserText);
        Assert.Equal(T._("Нужно войти снова."), new ApiError(ApiError.TokenRevoked, "", 401, null).UserText);
        Assert.Equal(T._("Что-то пошло не так. Попробуйте ещё раз."), new ApiError("SOMETHING_NEW", "", 400, null).UserText);
    }
}
