using System.Collections.ObjectModel;
using System.Diagnostics;
using System.Net.Http;
using System.Net.NetworkInformation;
using System.Text;
using System.Windows;
using System.Windows.Media;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Microsoft.Win32;
using PVpn.Core.Config;
using PVpn.Core.Cores;
using PVpn.Core.Diagnostics;
using PVpn.Core.Ipc;
using PVpn.Core.Net;
using PVpn.Core.Profiles;
using PVpn.Core.Sessions;
using PVpn.Core.SystemProxy;

namespace PVpn.App;

public sealed partial class MainViewModel
{
    // ───────────── Серверы ─────────────

    private void LoadProfiles()
    {
        try
        {
            data = store.Load();
        }
        catch (Exception e) when (e is System.Security.Cryptography.CryptographicException or System.Text.Json.JsonException or IOException)
        {
            Notice = $"Не удалось прочитать список серверов: {e.Message}";
            data = new ProfileData();
        }
        var selectedId = SelectedProfile?.Profile.Id ?? data.SelectedId;
        Profiles.Clear();
        for (var i = 0; i < data.Profiles.Count; i++)
        {
            var p = data.Profiles[i];
            Profiles.Add(new ProfileItem(p, data.Subscriptions.FirstOrDefault(s => s.Id == p.SubscriptionId)?.Name, i));
        }
        foreach (var auto in BuildAutoItems())
            Profiles.Add(auto);
        SelectedProfile = Profiles.FirstOrDefault(p => p.Profile.Id == selectedId) ?? Profiles.FirstOrDefault();
        LoadSubscriptions();
    }

    /// <summary>
    /// Пока VPN включён или подключается, выделение в списке стоит на его сервере: одиночный щелчок по другому
    /// серверу его не переносит (переключает только двойной щелчок, <see cref="SwitchServerAsync"/>).
    /// </summary>
    partial void OnSelectedProfileChanged(ProfileItem? oldValue, ProfileItem? newValue)
    {
        if (!movingSelection && oldValue != null && newValue != null && State is UiState.On or UiState.Busy)
        {
            var active = Profiles.FirstOrDefault(p => ProfileData.PlainName(p.Profile.Name) == ConnectedServer) ?? oldValue;
            if (newValue != active)
            {
                System.Windows.Application.Current?.Dispatcher.BeginInvoke(() => MoveSelection(active));
                return;
            }
        }
        OnSelectedProfileChangedCore(newValue);
    }

    private bool movingSelection;

    private void MoveSelection(ProfileItem item)
    {
        movingSelection = true;
        try
        {
            SelectedProfile = item;
        }
        finally
        {
            movingSelection = false;
        }
    }

    private void OnSelectedProfileChangedCore(ProfileItem? value)
    {
        OnPropertyChanged(nameof(CurrentServer));
        UpdateAnnounce();
        if (value == null || data.SelectedId == value.Profile.Id)
            return;
        data.SelectedId = value.Profile.Id;
        TrySaveProfiles();
    }

    [RelayCommand]
    private void ImportClipboard()
    {
        string text;
        try
        {
            // Картинка в буфере (снимок экрана с QR-кодом) — читаем коды с неё.
            text = Clipboard.ContainsImage() && Clipboard.GetImage() is { } image
                ? string.Join(Environment.NewLine, QrCodes.Decode(image))
                : Clipboard.ContainsText() ? Clipboard.GetText() : "";
        }
        catch (System.Runtime.InteropServices.ExternalException)
        {
            text = "";
        }
        if (string.IsNullOrWhiteSpace(text))
        {
            Notice = "В буфере обмена нет ссылок или QR-кода. Скопируйте ссылки vless:// или hy2:// (или картинку с QR) и нажмите ещё раз.";
            return;
        }
        // Ссылка действия от панели или бота (…://onadd/…, …://routing/onadd/…).
        if (IsAppLink(text))
        {
            _ = HandleLinkAsync(text.Trim(), fromOutside: false);
            return;
        }
        // Одна ссылка https:// — это ссылка подписки.
        if (Uri.TryCreate(text.Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            _ = AddSubscriptionAsync(text.Trim());
            return;
        }
        Import(text);
    }

    [RelayCommand]
    private void ImportFile()
    {
        var dialog = new OpenFileDialog
        {
            Title = "Файл со ссылками или картинка с QR-кодом",
            Filter = "Ссылки или QR (*.txt;*.png;*.jpg;*.jpeg;*.bmp)|*.txt;*.png;*.jpg;*.jpeg;*.bmp|Все файлы|*.*",
        };
        if (dialog.ShowDialog() != true)
            return;
        try
        {
            var ext = Path.GetExtension(dialog.FileName).ToLowerInvariant();
            if (ext is ".png" or ".jpg" or ".jpeg" or ".bmp")
            {
                var image = new System.Windows.Media.Imaging.BitmapImage();
                image.BeginInit();
                image.CacheOption = System.Windows.Media.Imaging.BitmapCacheOption.OnLoad;
                image.UriSource = new Uri(dialog.FileName);
                image.EndInit();
                ImportQr(QrCodes.Decode(image), "на картинке");
            }
            else
                Import(File.ReadAllText(dialog.FileName));
        }
        catch (Exception e) when (e is IOException or NotSupportedException or UnauthorizedAccessException)
        {
            Notice = $"Не удалось прочитать файл: {e.Message}";
        }
    }

    /// <summary>QR-коды на экране: окно PVpn сворачивается, чтобы не закрывать код.</summary>
    [RelayCommand]
    private async Task ImportQrScreenAsync()
    {
        var window = Application.Current.MainWindow;
        var restore = window is { IsVisible: true, WindowState: not WindowState.Minimized };
        if (restore)
        {
            window!.WindowState = WindowState.Minimized;
            await Task.Delay(400);
        }
        try
        {
            ImportQr(QrCodes.DecodeScreens(), "на экране");
        }
        finally
        {
            if (restore)
            {
                window!.WindowState = WindowState.Normal;
                window.Activate();
            }
        }
    }

    private void ImportQr(IReadOnlyList<string> codes, string where)
    {
        if (codes.Count == 0)
        {
            Notice = $"QR-код {where} не найден. Откройте код крупнее и попробуйте ещё раз.";
            return;
        }
        // В коде может быть и ссылка подписки.
        if (codes.Count == 1 && Uri.TryCreate(codes[0].Trim(), UriKind.Absolute, out var uri) && (uri.Scheme == Uri.UriSchemeHttps || uri.Scheme == Uri.UriSchemeHttp))
        {
            _ = AddSubscriptionAsync(codes[0].Trim());
            return;
        }
        Import(string.Join(Environment.NewLine, codes));
    }

    [RelayCommand(CanExecute = nameof(CanShowQr))]
    private void EditProfile()
    {
        if (SelectedProfile is not { } item)
            return;
        var dialog = new EditProfileWindow(item.Profile, item.Profile.SubscriptionId != null) { Owner = Application.Current.MainWindow };
        if (dialog.ShowDialog() != true || dialog.Result is not { } edited)
            return;
        var index = data.Profiles.FindIndex(p => p.Id == edited.Id);
        if (index < 0)
            return;
        data.Profiles[index] = edited;
        if (TrySaveProfiles())
            Notice = State == UiState.On && ConnectedServer == item.Name ? "Сервер изменён. Новая ссылка подействует при следующем подключении." : "Сервер изменён.";
        LoadProfiles();
    }

    // У сервера с полным конфигом из подписки ссылки нет — ни QR, ни правки ссылки.
    private bool CanShowQr() => SelectedProfile is { Profile: not XrayConfigProfile };

    [RelayCommand(CanExecute = nameof(CanShowQr))]
    private void ShowQr()
    {
        if (SelectedProfile is not { } item)
            return;
        new QrWindow(item.Name, item.Profile.SourceLink) { Owner = Application.Current.MainWindow }.ShowDialog();
    }

    private void Import(string text)
    {
        var result = LinkParser.ParseMany(text);
        var added = 0;
        Profile? last = null;
        foreach (var p in result.Profiles)
        {
            if (data.Upsert(p))
                added++;
            last = p;
        }
        if (result.Profiles.Count > 0 && !TrySaveProfiles())
            return;
        LoadProfiles();
        if (last != null)
            SelectedProfile = Profiles.FirstOrDefault(i => string.Equals(i.Profile.Name, last.Name, StringComparison.OrdinalIgnoreCase));

        var sb = new StringBuilder();
        sb.Append(result.Profiles.Count == 0 ? "Ссылок не найдено." : $"Серверов: новых {added}, обновлено {result.Profiles.Count - added}.");
        foreach (var e in result.Errors.Take(3))
            sb.Append($" Строка {e.Line}: {e.Message}.");
        if (result.Errors.Count > 3)
            sb.Append($" И ещё ошибок: {result.Errors.Count - 3}.");
        Notice = sb.ToString();
    }

    private bool CanDeleteProfile() => SelectedProfile is { IsAuto: false };

    [RelayCommand(CanExecute = nameof(CanDeleteProfile))]
    private void DeleteProfile()
    {
        if (SelectedProfile is not { } item)
            return;
        if (MessageBox.Show($"Удалить сервер «{item.Name}»?", Brand.Name, MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        data.Profiles.RemoveAll(p => p.Id == item.Profile.Id);
        if (data.SelectedId == item.Profile.Id)
            data.SelectedId = null;
        TrySaveProfiles();
        SelectedProfile = null;
        LoadProfiles();
    }

    private bool TrySaveProfiles()
    {
        try
        {
            store.Save(data);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Security.Cryptography.CryptographicException)
        {
            Notice = $"Не удалось сохранить список серверов: {e.Message}";
            return false;
        }
    }

    /// <summary>Сколько серверов проверяется одновременно (у каждого свой временный xray).</summary>
    private const int ParallelDelayChecks = 4;
    private bool checkingDelays;

    /// <summary>
    /// Настоящая задержка: временный xray и запрос generate_204 через сервер (как у браузера).
    /// Серверы проверяются по несколько сразу: по одному подписка на 20 серверов проверялась бы минуты.
    /// </summary>
    [RelayCommand]
    private async Task CheckDelaysAsync()
    {
        if (checkingDelays)
            return;
        CorePaths cores;
        try
        {
            cores = CorePaths.Resolve(allowDevFallback: true);
        }
        catch (FileNotFoundException e)
        {
            Notice = e.Message;
            return;
        }
        checkingDelays = true;
        var items = Profiles.ToList();
        foreach (var item in items)
            item.Checking = true;
        try
        {
            // Один xray на все серверы; не запустился (например, из-за одного кривого сервера) — по одному, как раньше.
            var byId = items.ToDictionary(i => i.Profile.Id);
            try
            {
                // Автовыбор не пингуется сам: его задержка — лучшая из его серверов (ShowAutoDelays).
                await BatchPing.MeasureAsync(items.Where(i => !i.IsAuto).Select(i => new PingTarget(i.Profile, BypassFor(i.Profile, settings.Bypass))).ToList(), cores,
                    (id, delay) => Application.Current?.Dispatcher.BeginInvoke(() =>
                    {
                        if (byId.TryGetValue(id, out var item))
                        {
                            item.Delay = delay;
                            item.Checking = false;
                        }
                    }));
                return;
            }
            catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception)
            {
                AppendLog("app", $"Пинг одним процессом не вышел ({e.Message.Split('\n')[0]}), проверяю по одному");
            }
            await PingOneByOneAsync(items.Where(i => i.Checking && !i.IsAuto).ToList(), cores);
        }
        finally
        {
            foreach (var item in items)
                item.Checking = false;
            ShowAutoDelays();
            checkingDelays = false;
        }
    }

    private async Task PingOneByOneAsync(IReadOnlyList<ProfileItem> items, CorePaths cores)
    {
        using var limit = new SemaphoreSlim(ParallelDelayChecks);
        {
            await Task.WhenAll(items.Select(async item =>
            {
                await limit.WaitAsync();
                try
                {
                    await using var session = await ProxySession.StartAsync(item.Profile, cores,
                        new ProxySessionOptions { SocksPort = 0, HttpPort = -1, Bypass = BypassFor(item.Profile, settings.Bypass), UseServerRouting = settings.UseServerRouting }, (_, _) => { });
                    item.Delay = await DelayTester.HttpDelayViaSocksAsync(session.SocksPort, TimeSpan.FromSeconds(10));
                }
                catch (Exception e) when (e is InvalidOperationException or IOException or System.ComponentModel.Win32Exception or ConfigException)
                {
                    item.Delay = new DelayResult(null, e.Message);
                }
                finally
                {
                    item.Checking = false;
                    limit.Release();
                }
            }));
        }
    }
}
