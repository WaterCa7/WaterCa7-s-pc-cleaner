using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using PcCleaner.Core.Account;
using PcCleaner.Core.Localization;

namespace PcCleaner.Core.AccountWindows;

/// <summary>
/// %LOCALAPPDATA%\WaterCa7sPcCleaner\account.bin: the JSON of <see cref="AccountData"/> encrypted by DPAPI for the current user
/// (another account of the PC and another PC cannot read it). Written through a temp file; a file that cannot be read is empty.
/// </summary>
public sealed class DpapiAccountStore : IAccountStore
{
    internal static readonly byte[] Entropy = Encoding.ASCII.GetBytes("WaterCa7sPcCleaner.account.v1");

    private readonly string _file;
    private readonly object _lock = new();

    public DpapiAccountStore(string? file = null) => _file = file ?? Path.Combine(AppPaths.DataDir, "account.bin");

    public AccountData Load()
    {
        lock (_lock)
        {
            try
            {
                if (!File.Exists(_file))
                    return new AccountData();
                var json = ProtectedData.Unprotect(File.ReadAllBytes(_file), Entropy, DataProtectionScope.CurrentUser);
                return JsonSerializer.Deserialize<AccountData>(json) ?? new AccountData();
            }
            catch (Exception ex) when (ex is CryptographicException or JsonException or IOException or UnauthorizedAccessException)
            {
                Log.Warn(T.F("Аккаунт: сохранённый вход не прочитан ({0}) — нужно войти снова", ex.GetType().Name));
                return new AccountData();
            }
        }
    }

    public void Save(AccountData data)
    {
        lock (_lock)
        {
            AppPaths.EnsureDir(Path.GetDirectoryName(_file)!);
            var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(data), Entropy, DataProtectionScope.CurrentUser);
            var temp = _file + ".tmp";
            File.WriteAllBytes(temp, bytes);
            File.Move(temp, _file, overwrite: true);
        }
    }
}
