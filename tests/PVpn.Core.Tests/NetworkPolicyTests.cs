using System.Net;
using PVpn.Core.Net;

namespace PVpn.Core.Tests;

public class NetworkPolicyTests
{
    private static readonly string[] Trusted = ["Keenetic 3", "Офис"];

    [Theory]
    // Не доверенная сеть: автоподключение, если включено и VPN выключен.
    [InlineData("Кафе", true, true, false, false, AutoConnectAction.Connect)]
    [InlineData("Кафе", false, true, false, false, AutoConnectAction.None)]
    [InlineData("Кафе", true, true, true, false, AutoConnectAction.None)]
    // Доверенная (без учёта регистра): отключить, если так настроено.
    [InlineData("keenetic 3", true, true, true, false, AutoConnectAction.Disconnect)]
    [InlineData("Keenetic 3", true, false, true, false, AutoConnectAction.None)]
    [InlineData("Keenetic 3", true, true, false, false, AutoConnectAction.None)]
    // Пользователь сам решил в этой сети — не спорим.
    [InlineData("Кафе", true, true, false, true, AutoConnectAction.None)]
    [InlineData("Keenetic 3", true, true, true, true, AutoConnectAction.None)]
    public void Decides(string network, bool autoConnect, bool disconnectOnTrusted, bool vpnActive, bool userOverride, AutoConnectAction expected) =>
        Assert.Equal(expected, AutoConnectPolicy.Decide([network], Trusted, autoConnect, disconnectOnTrusted, vpnActive, userOverride));

    [Fact]
    public void NoNetworkMeansNoAction() =>
        Assert.Equal(AutoConnectAction.None, AutoConnectPolicy.Decide([], Trusted, true, true, false, false));

    [Fact]
    public void TrustedIfAnyNetworkIsTrusted() =>
        Assert.Equal(AutoConnectAction.Disconnect, AutoConnectPolicy.Decide(["Сеть", "Офис"], Trusted, true, true, true, false));

    [Fact]
    public void KeyIgnoresOrderAndCase() =>
        Assert.Equal(AutoConnectPolicy.Key(["Сеть", "Офис"]), AutoConnectPolicy.Key(["офис", "СЕТЬ"]));

    [Theory]
    [InlineData(HttpStatusCode.OK, "Microsoft Connect Test", InternetState.Online)]
    [InlineData(HttpStatusCode.OK, "<html>Войдите в сеть</html>", InternetState.Portal)]
    [InlineData(HttpStatusCode.Found, null, InternetState.Portal)]
    [InlineData(HttpStatusCode.NetworkAuthenticationRequired, null, InternetState.Portal)]
    [InlineData(HttpStatusCode.BadGateway, null, InternetState.Offline)]
    public void ClassifiesProbe(HttpStatusCode code, string? body, InternetState expected) =>
        Assert.Equal(expected, CaptivePortal.Classify(code, body));

    [Fact]
    public void ListsNetworksWithoutThrowing()
    {
        var names = NetworkNames.GetConnected();
        Assert.All(names, n => Assert.False(string.IsNullOrWhiteSpace(n)));
    }
}
