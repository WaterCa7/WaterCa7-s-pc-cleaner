using PcCleaner.Core.Account;
using PcCleaner.Modules.Profile;
using Xunit;

namespace PcCleaner.Tests.Profile;

public class SignInErrorsTests
{
    public SignInErrorsTests() => TestEnvironment.Init();

    [Fact]
    public void InvalidInput_ShowsTheServersWords_NotTheEmailHint()
    {
        // The window checks the e-mail before sending: an INVALID_INPUT that still comes back is about another field (the device's).
        var error = new ApiError(ApiError.InvalidInput, "Неверное поле hw_hash", 400, null);

        Assert.Equal("Неверное поле hw_hash", SignInErrors.Text(error));
    }

    [Fact]
    public void InvalidInput_WithoutAMessage_HasTheGeneralText()
    {
        var error = new ApiError(ApiError.InvalidInput, "", 400, null);

        Assert.Equal(error.UserText, SignInErrors.Text(error));
        Assert.DoesNotContain("почты", SignInErrors.Text(error));
    }

    [Fact]
    public void EmailInvalid_StillPointsAtTheEmail()
        => Assert.Equal("Проверьте адрес почты.", SignInErrors.Text(new ApiError(ApiError.EmailInvalid, "msg", 422, null)));
}
