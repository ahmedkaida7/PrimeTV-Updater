using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;
using Apps2Samsung.Services;
using Apps2Samsung.Mobile.Services;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Pages;

public sealed class PrimeTvUpdaterPage : ContentPage
{
    private const string SavedTvKey = "primetv.updater.tv";
    private readonly INetworkService _networkService;
    private readonly CertificateProvisioner _certProvisioner;
    private readonly WgtInstaller _installer;

    private readonly Picker _tvPicker = new() { Title = "Samsung TV" };
    private readonly Entry _manualIp = new() { Placeholder = "TV IP address (optional)" };
    private readonly Button _scan = new() { Text = "Find TV" };
    private readonly Button _pick = new() { Text = "Select PrimeTV WGT" };
    private readonly Button _install = new() { Text = "Update TV", FontAttributes = FontAttributes.Bold };
    private readonly Label _selected = new() { Text = "No WGT selected", FontSize = 13 };
    private readonly Label _status = new() { Text = "Ready", FontSize = 14 };
    private readonly ProgressBar _progress = new() { Progress = 0 };

    private readonly List<NetworkDevice?> _devices = new();
    private readonly List<string> _ips = new();
    private string? _wgtPath;
    private bool _busy;

    public PrimeTvUpdaterPage(INetworkService networkService, CertificateProvisioner certProvisioner, WgtInstaller installer)
    {
        _networkService = networkService;
        _certProvisioner = certProvisioner;
        _installer = installer;

        Title = "PrimeTV Updater";
        BackgroundColor = Color.FromArgb("#08111F");

        _scan.Clicked += async (_, _) => await ScanAsync();
        _pick.Clicked += async (_, _) => await PickWgtAsync();
        _install.Clicked += async (_, _) => await InstallAsync();
        _tvPicker.SelectedIndexChanged += (_, _) => RememberTv();

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(24, 40),
                Spacing = 16,
                Children =
                {
                    new Label { Text = "PrimeTV Updater", FontSize = 32, FontAttributes = FontAttributes.Bold, TextColor = Colors.White },
                    new Label { Text = "Install a PrimeTV update from your phone directly to your Samsung TV.", FontSize = 15, TextColor = Color.FromArgb("#9DB4D0") },
                    new BoxView { HeightRequest = 1, Color = Color.FromArgb("#20314A") },
                    _tvPicker,
                    _manualIp,
                    _scan,
                    _pick,
                    _selected,
                    _install,
                    _progress,
                    _status,
                    new Label { Text = "1. Download the PrimeTV .wgt to your phone\n2. Tap Select PrimeTV WGT\n3. Select your TV\n4. Tap Update TV", FontSize = 13, TextColor = Color.FromArgb("#8294AA") }
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var saved = Preferences.Default.Get(SavedTvKey, "");
        if (!string.IsNullOrWhiteSpace(saved)) _manualIp.Text = saved;
        await ScanAsync();
    }

    private void SetStatus(string text) => MainThread.BeginInvokeOnMainThread(() => _status.Text = text);

    private async Task ScanAsync()
    {
        if (_busy) return;
        _busy = true;
        SetButtons(false);
        try
        {
            SetStatus("Scanning for Samsung TVs…");
            var found = await TizenDeveloperInfo.ScanAsync(_networkService);
            _devices.Clear();
            _ips.Clear();
            var labels = new List<string>();
            foreach (var d in found)
            {
                _devices.Add(d);
                _ips.Add(d.IpAddress);
                labels.Add(d.DisplayText);
            }
            _tvPicker.ItemsSource = labels;
            var saved = Preferences.Default.Get(SavedTvKey, "");
            var idx = _ips.IndexOf(saved);
            if (idx >= 0) _tvPicker.SelectedIndex = idx;
            else if (_ips.Count > 0) _tvPicker.SelectedIndex = 0;
            SetStatus(_ips.Count > 0 ? $"Found {_ips.Count} TV(s)." : "No TV found automatically. Enter the TV IP manually.");
        }
        catch (Exception ex) { SetStatus("TV scan failed: " + ex.Message); }
        finally { _busy = false; SetButtons(true); }
    }

    private async Task PickWgtAsync()
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Select PrimeTV .wgt" });
            if (result is null) return;
            if (!result.FileName.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("Please select a .wgt file.");
                return;
            }
            var dest = Path.Combine(FileSystem.CacheDirectory, "PrimeTV-selected.wgt");
            await using var input = await result.OpenReadAsync();
            await using var output = File.Create(dest);
            await input.CopyToAsync(output);
            _wgtPath = dest;
            _selected.Text = "Selected: " + result.FileName;
            SetStatus("WGT ready. Tap Update TV.");
        }
        catch (Exception ex) { SetStatus("Could not open WGT: " + ex.Message); }
    }

    private string? ResolveTvIp()
    {
        if (_tvPicker.SelectedIndex >= 0 && _tvPicker.SelectedIndex < _ips.Count)
            return _ips[_tvPicker.SelectedIndex];
        var manual = (_manualIp.Text ?? "").Trim();
        return System.Net.IPAddress.TryParse(manual, out _) ? manual : null;
    }

    private void RememberTv()
    {
        var ip = ResolveTvIp();
        if (ip is not null) Preferences.Default.Set(SavedTvKey, ip);
    }

    private async Task InstallAsync()
    {
        if (_busy) return;
        if (string.IsNullOrWhiteSpace(_wgtPath) || !File.Exists(_wgtPath))
        {
            SetStatus("Select the PrimeTV .wgt first.");
            return;
        }
        var tvIp = ResolveTvIp();
        if (tvIp is null)
        {
            SetStatus("Select a TV or enter its IP address.");
            return;
        }
        Preferences.Default.Set(SavedTvKey, tvIp);
        _busy = true;
        SetButtons(false);
        _progress.Progress = 0.1;
        try
        {
            NetworkDevice? device = null;
            var idx = _ips.IndexOf(tvIp);
            if (idx >= 0 && idx < _devices.Count) device = _devices[idx];

            var guardResult = InstallGuards.Evaluate(device,
                new InstallGuardOptions
                {
                    LocalIps = SafeLocalIps(),
                    ConfiguredLocalIp = NetworkInfo.GetLocalIPv4()
                }, _networkService);

            foreach (var guard in guardResult.Guards)
            {
                var go = await DisplayAlert(guard.DefaultTitle, guard.DefaultMessageWithDetail, "Continue", "Stop");
                if (!go) return;
            }
            tvIp = guardResult.CorrectedTvIp ?? tvIp;
            _progress.Progress = 0.35;

            SetStatus("Preparing TV certificate…");
            var needsPartner = WgtPrivileges.RequiresPartner(_wgtPath);
            var cert = await _certProvisioner.ProvisionAsync(tvIp, needsPartner, SetStatus);
            _progress.Progress = 0.65;

            SetStatus("Installing PrimeTV on " + tvIp + "…");
            await _installer.InstallAsync(tvIp, _wgtPath, cert, SetStatus);
            _progress.Progress = 1.0;
            SetStatus("✓ PrimeTV update installed successfully.");
        }
        catch (Exception ex)
        {
            SetStatus("Install failed: " + ex.Message);
        }
        finally
        {
            _busy = false;
            SetButtons(true);
        }
    }

    private List<string> SafeLocalIps()
    {
        try { return _networkService.GetRelevantLocalIPs().Select(ip => ip.ToString()).ToList(); }
        catch { return new List<string>(); }
    }

    private void SetButtons(bool enabled)
    {
        _scan.IsEnabled = enabled;
        _pick.IsEnabled = enabled;
        _install.IsEnabled = enabled;
    }
}
