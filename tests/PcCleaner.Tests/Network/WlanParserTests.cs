using System.Text;
using PcCleaner.Core;
using PcCleaner.Modules.Network;
using Xunit;

namespace PcCleaner.Tests.Network;

public class WlanParserTests
{
    // Real output of an English Windows 11.
    private const string English = """
        There is 1 interface on the system:

            Name                   : Wireless Network 2
            Description            : Realtek 8812BU Wireless LAN 802.11ac USB NIC
            GUID                   : 0f37372b-0046-4e4e-baad-ad669ad43102
            Physical address       : 02:00:00:00:00:01
            Interface type         : Primary
            State                  : connected
            SSID                   : HomeNet 5G
            AP BSSID               : 02:00:00:00:00:02
            Band                   : 5 GHz
            Channel                : 132
            Connected Akm-cipher   : [ akm = 00-0f-ac:02, cipher =  00-0f-ac:04 ]
            Network type           : Infrastructure
            Radio type             : 802.11ac
            Authentication         : WPA2-Personal
            Cipher                 : CCMP
            Connection mode        : Auto Connect
            Receive rate (Mbps)    : 866.7
            Transmit rate (Mbps)   : 650
            Signal                 : 100%
            Rssi                   : -43
            Profile                : HomeNet 5G
            QoS MSCS Configured         : 0

            Hosted network status  : Not available
        """;

    // Real output of a Russian Windows 11 (keys without a space before the colon are real).
    private const string Russian = """
        В системе 1 интерфейс:

            Имя:                                Беспроводная сеть 2
            Описание:                       Realtek 8812BU Wireless LAN 802.11ac USB NIC
            Идентификатор GUID:   0f37372b-0046-4e4e-baad-ad669ad43102
            Физический адрес:       02:00:00:00:00:01
            Тип интерфейса         : Основной
            Состояние:                     Подключено
            SSID                   : Домашняя сеть
            AP BSSID               : 02:00:00:00:00:02
            Диапазон                   : 2,4 ГГц
            Канал                : 6
            Подключенный шифр Akm : [ akm = 00-0f-ac:02, шифр = 00-0f-ac:04 ]
            Тип сети:                          Инфраструктура
            Тип радио:                        802.11n
            Проверка подлинности:  WPA2-Personal
            Шифр                 : CCMP
            Режим подключения        : Автоматическое подключение
            Скорость приема (Мбит/с):    144,4
            Скорость передачи (Мбит/с): 72
            Сигнал:                                     38%
            Rssi                   : -78
            Профиль                : Домашняя сеть
        """;

    [Fact]
    public void Parse_English()
    {
        var wifi = Assert.Single(WlanParser.Parse(English));
        Assert.Equal("Wireless Network 2", wifi.Name);
        Assert.Equal("0f37372b-0046-4e4e-baad-ad669ad43102", wifi.Guid);
        Assert.True(wifi.IsConnected);
        Assert.Equal("HomeNet 5G", wifi.Ssid);
        Assert.Equal("02:00:00:00:00:02", wifi.Bssid);
        Assert.Equal(5, wifi.BandGhz);
        Assert.Equal(132, wifi.Channel);
        Assert.Equal("802.11ac", wifi.RadioType);
        Assert.Equal("WPA2-Personal", wifi.Authentication);
        Assert.Equal(866.7, wifi.ReceiveMbps);
        Assert.Equal(650, wifi.TransmitMbps);
        Assert.Equal(100, wifi.SignalPercent);
        Assert.Equal(-43, wifi.Rssi);
        Assert.Equal("HomeNet 5G", wifi.Profile);
    }

    [Fact]
    public void Parse_Russian()
    {
        var wifi = Assert.Single(WlanParser.Parse(Russian));
        Assert.Equal("Беспроводная сеть 2", wifi.Name);
        Assert.Equal("0f37372b-0046-4e4e-baad-ad669ad43102", wifi.Guid);
        Assert.True(wifi.IsConnected);
        Assert.Equal("Домашняя сеть", wifi.Ssid);
        Assert.Equal(2.4, wifi.BandGhz);
        Assert.Equal(6, wifi.Channel);
        Assert.Equal("802.11n", wifi.RadioType);
        Assert.Equal("WPA2-Personal", wifi.Authentication);
        Assert.Equal(144.4, wifi.ReceiveMbps);
        Assert.Equal(72, wifi.TransmitMbps);
        Assert.Equal(38, wifi.SignalPercent);
    }

    [Fact]
    public void Parse_MultipleInterfaces_DisconnectedOneHasNoSignal()
    {
        const string text = """
            There are 2 interfaces on the system:

                Name                   : Wi-Fi
                Description            : Intel(R) Wi-Fi 6 AX201 160MHz
                GUID                   : 11111111-2222-3333-4444-555555555555
                State                  : disconnected
                Radio status           : Hardware On
                                         Software On

                Name                   : Wi-Fi 2
                Description            : TP-Link Wireless USB Adapter
                GUID                   : aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee
                State                  : connected
                SSID                   : Cafe Free
                BSSID                  : 00:11:22:33:44:55
                Radio type             : 802.11g
                Authentication         : Open
                Cipher                 : None
                Channel                : 11
                Receive rate (Mbps)    : 54
                Transmit rate (Mbps)   : 54
                Signal                 : 61%
                Profile                : Cafe Free
            """;

        var list = WlanParser.Parse(text);

        Assert.Equal(2, list.Count);
        Assert.False(list[0].IsConnected);
        Assert.Equal("disconnected", list[0].State);
        Assert.Null(list[0].SignalPercent);
        Assert.True(list[1].IsConnected);
        Assert.Equal("Cafe Free", list[1].Ssid);
        Assert.Equal("00:11:22:33:44:55", list[1].Bssid);
        Assert.Equal(2.4, list[1].BandGhz); // no "Band" line: guessed from channel 11
        Assert.Equal(61, list[1].SignalPercent);
        Assert.True(NetworkRules.IsWeakWifiSecurity(list[1].Authentication, list[1].Cipher));
    }

    [Fact]
    public void Parse_RussianDisconnected()
    {
        const string text = """
            В системе 1 интерфейс:

                Имя:                   Беспроводная сеть
                Описание:              Intel(R) Wi-Fi 6 AX201 160MHz
                Состояние:             Отключено
            """;

        var wifi = Assert.Single(WlanParser.Parse(text));
        Assert.False(wifi.IsConnected);
        Assert.Null(new WifiReport { Interfaces = new[] { wifi } }.Connected);
    }

    // German Windows: "Status : Verbunden" (the finding's sample).
    private const string German = """
        Es ist 1 Schnittstelle auf dem System vorhanden:

            Name                   : WLAN
            Beschreibung           : Intel(R) Wi-Fi 6 AX201 160MHz
            GUID                   : 0f37372b-0046-4e4e-baad-ad669ad43102
            Physische Adresse      : 02:00:00:00:00:01
            Status                 : Verbunden
            SSID                   : Heimnetz
            AP BSSID               : 02:00:00:00:00:02
            Band                   : 2,4 GHz
            Kanal                  : 6
            Funktyp                : 802.11n
            Authentifizierung      : Offen
            Verschlüsselung        : Keine
            Signal                 : 92%
            Profil                 : Heimnetz
        """;

    [Fact]
    public void Parse_German_IsConnected()
    {
        var wifi = Assert.Single(WlanParser.Parse(German));
        Assert.True(wifi.IsConnected);
        Assert.Equal("WLAN", wifi.Name);
        Assert.Equal("Heimnetz", wifi.Ssid);
        Assert.Equal(92, wifi.SignalPercent);
        Assert.Equal(2.4, wifi.BandGhz);
        Assert.True(NetworkRules.IsWeakWifiSecurity(wifi.Authentication, wifi.Cipher)); // "Offen" = open network
        Assert.NotNull(new WifiReport { Interfaces = new[] { wifi } }.Connected);
    }

    [Fact]
    public void Parse_Ukrainian_FindsTheInterface()
    {
        const string text = """
            У системі 1 інтерфейс:

                Ім'я                   : Бездротова мережа
                Опис                   : Realtek 8812BU Wireless LAN 802.11ac USB NIC
                GUID                   : 0f37372b-0046-4e4e-baad-ad669ad43102
                Стан                   : підключено
                SSID                   : Дім
                Канал                  : 132
                Сигнал                 : 81%
                Профіль                : Дім
            """;

        var wifi = Assert.Single(WlanParser.Parse(text));
        Assert.Equal("Бездротова мережа", wifi.Name);
        Assert.True(wifi.IsConnected);
        Assert.Equal("Дім", wifi.Ssid);
        Assert.Equal(81, wifi.SignalPercent);
        Assert.Equal(5, wifi.BandGhz); // guessed from channel 132
    }

    [Fact]
    public void Parse_UnknownLanguage_UsesTheFirstLineAsName_AndTheSignalAsConnected()
    {
        const string text = """
            Il existe 2 interfaces sur le système :

                Nom                    : Wi-Fi
                Description            : Intel(R) Wi-Fi 6 AX201 160MHz
                GUID                   : 11111111-2222-3333-4444-555555555555
                État                   : déconnecté

                Nom                    : Wi-Fi 2
                Description            : TP-Link Wireless USB Adapter
                Identificateur         : aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee
                État                   : connecté
                SSID                   : Maison
                Canal                  : 36
                Signal                 : 88 %
            """;

        var list = WlanParser.Parse(text);

        Assert.Equal(2, list.Count);
        Assert.Equal("Wi-Fi", list[0].Name);
        Assert.False(list[0].IsConnected); // no signal line: not connected
        Assert.Equal("Wi-Fi 2", list[1].Name);
        Assert.Equal("aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee", list[1].Guid); // recognised by the value
        Assert.True(list[1].IsConnected);
        Assert.Equal(88, list[1].SignalPercent);
    }

    [Fact]
    public void Parse_LocationMessage_IsNotAnInterface()
    {
        const string text = """
            Network shell commands need location permission to access WLAN information. Turn on Location services on the Location page in Privacy & security settings.

            Here is the URI for the Location page in the Settings app:
                ms-settings:privacy-location
            """;
        Assert.Empty(WlanParser.Parse(text));
        Assert.True(WlanParser.IsLocationBlocked(text));
    }

    [Fact]
    public void Parse_EmptyOrGarbage_ReturnsNothing()
    {
        Assert.Empty(WlanParser.Parse(null));
        Assert.Empty(WlanParser.Parse(""));
        Assert.Empty(WlanParser.Parse("The Wireless AutoConfig Service (wlansvc) is not running."));
    }

    [Theory]
    [InlineData("Network shell commands need location permission to access WLAN information.", true)]
    [InlineData("Для доступа к сведениям о WLAN командам сетевой оболочки требуется разрешение на доступ к местоположению.", true)]
    [InlineData("There is 1 interface on the system:", false)]
    public void IsLocationBlocked(string output, bool expected)
        => Assert.Equal(expected, WlanParser.IsLocationBlocked(output));

    [Theory]
    [InlineData("connected", true)]
    [InlineData("Подключено", true)]
    [InlineData("disconnected", false)]
    [InlineData("Отключено", false)]
    [InlineData("authenticating", false)]
    [InlineData("Verbunden", true)]
    [InlineData("Підключено", true)]
    [InlineData("Відключено", false)]
    [InlineData("Getrennt", false)]
    public void IsConnectedState(string state, bool expected)
        => Assert.Equal(expected, WlanParser.IsConnectedState(state));

    [Theory]
    [InlineData(1, 2.4)]
    [InlineData(13, 2.4)]
    [InlineData(36, 5.0)]
    [InlineData(165, 5.0)]
    public void GuessBand(int channel, double ghz) => Assert.Equal(ghz, WlanParser.GuessBand(channel));

    [Fact]
    public void DecodeConsoleOutput_DetectsUtf8AndFallsBackToOem()
    {
        var text = "Сигнал: 100%";
        Assert.Equal(text, NetworkProbes.DecodeConsoleOutput(Encoding.UTF8.GetBytes(text)));

        var oem = ProcessRunner.OemEncoding;
        var oemBytes = oem.GetBytes(text);
        Assert.Equal(oem.GetString(oemBytes), NetworkProbes.DecodeConsoleOutput(oemBytes));
    }
}
