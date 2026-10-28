using PcCleaner.Core.Account;
using PcCleaner.Tests.Account;

namespace PcCleaner.Tests.Profile;

/// <summary>An AccountService on the fake server with a clock and a delay that only moves the clock.</summary>
internal sealed class TestAccount : IDisposable
{
    public TestLicense Key { get; } = new();
    public FakeApi Fake { get; } = new();
    public MemoryAccountStore Store { get; } = new();
    public DateTimeOffset Now { get; set; } = TestLicense.Now;
    public List<string> Opened { get; } = new();

    public AccountService Service { get; }

    public TestAccount(string? signedInTicketJson = null)
    {
        if (signedInTicketJson is not null)
        {
            var t = Key.Sign(signedInTicketJson);
            Store.Data = new AccountData("tok", "user@example.com", TestLicense.DeviceId, TestLicense.Sub, t.Ticket, t.Signature, "inst-1");
        }
        Service = new AccountService(Api.Client(Fake), Store, new FixedHwHash(), "windows", "PC", "1.5.0", Key.Keys, () => Now,
            (d, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                Now += d;
                return Task.CompletedTask;
            });
    }

    public void Open(string url) => Opened.Add(url);

    public string LimitJson(params (long Id, string Name)[] devices)
    {
        var list = string.Join(",", devices.Select(d =>
            $"{{\"id\":{d.Id},\"name\":\"{d.Name}\",\"platform\":\"windows\",\"bound_at\":\"2026-10-01T10:00:00Z\",\"last_seen_at\":null,\"current\":false}}"));
        return $",\"devices\":[{list}],\"bind_limit\":{devices.Length},\"unbind_token\":\"{new string('a', 64)}\"";
    }

    public void Dispose() => Key.Dispose();
}
