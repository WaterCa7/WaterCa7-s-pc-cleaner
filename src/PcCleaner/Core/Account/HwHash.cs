using System.Security.Cryptography;
using System.Text;

namespace PcCleaner.Core.Account;

/// <summary>This computer's number for the license (<c>hw_hash</c>); the platform says what goes into it.</summary>
public interface IHwHash
{
    string Value { get; }
}

public static class HwHash
{
    /// <summary>SHA-256 (lowercase hex) of the parts joined by «|»; an empty part is «-». Windows: board serial|ProcessorId|system disk serial; Mac: the serial.</summary>
    public static string Of(params string?[] parts)
    {
        var text = string.Join("|", parts.Select(p => string.IsNullOrWhiteSpace(p) ? "-" : p.Trim()));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    }
}
