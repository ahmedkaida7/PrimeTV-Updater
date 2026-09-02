using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;
using Apps2Samsung.Services;
using Apps2Samsung.Mobile.Services;
using Apps2Samsung.Sdb;
using Microsoft.Maui.ApplicationModel;
using Microsoft.Maui.Storage;

namespace Apps2Samsung.Mobile.Pages;

public sealed class PrimeTvUpdaterPage : ContentPage
{
    private const string SavedTvKey = "primetv.updater.tv";

    private readonly INetworkService _networkService;
    private readonly CertificateProvisioner _certProvisioner;
    private readonly WgtInstaller _installer;
    private readonly ISdbEngine _sdb;

    private readonly Picker _tvPicker = new() { Title = "Samsung TV" };
    private readonly Entry _manualIp = new() { Placeholder = "TV IP address (optional)" };
    private readonly Label _headline = new()
    {
        Text = "Update Required",
        FontSize = 24,
        FontAttributes = FontAttributes.Bold,
        HorizontalTextAlignment = TextAlignment.Center,
        TextColor = Colors.White
    };
    private readonly Label _updateInfo = new()
    {
        Text = "Choose the new PrimeTV package once, then tap Update Now.",
        FontSize = 14,
        HorizontalTextAlignment = TextAlignment.Center,
        TextColor = Color.FromArgb("#A8B8CD")
    };
    private readonly Button _scan = new() { Text = "Find TV" };
    private readonly Button _pick = new() { Text = "Choose Update Package (.wgt)" };
    private readonly Button _install = new()
    {
        Text = "Update Now",
        FontAttributes = FontAttributes.Bold,
        BackgroundColor = Color.FromArgb("#D71920"),
        TextColor = Colors.White
    };
    private readonly Button _replace = new()
    {
        Text = "Replace Existing PrimeTV",
        FontAttributes = FontAttributes.Bold,
        BackgroundColor = Color.FromArgb("#A72121"),
        TextColor = Colors.White,
        IsVisible = false
    };
    private readonly Label _selected = new()
    {
        Text = "No update package selected",
        FontSize = 13,
        HorizontalTextAlignment = TextAlignment.Center,
        TextColor = Color.FromArgb("#A8B8CD")
    };
    private readonly Label _status = new()
    {
        Text = "Ready",
        FontSize = 14,
        HorizontalTextAlignment = TextAlignment.Center,
        TextColor = Colors.White
    };
    private readonly ProgressBar _progress = new() { Progress = 0 };

    private readonly List<NetworkDevice?> _devices = new();
    private readonly List<string> _ips = new();
    private string? _wgtPath;
    private string? _selectedName;
    private bool _busy;

    public PrimeTvUpdaterPage(
        INetworkService networkService,
        CertificateProvisioner certProvisioner,
        WgtInstaller installer,
        ISdbEngine sdb)
    {
        _networkService = networkService;
        _certProvisioner = certProvisioner;
        _installer = installer;
        _sdb = sdb;

        Title = "PrimeTV Updater";
        BackgroundColor = Color.FromArgb("#05070B");

        _scan.Clicked += async (_, _) => await ScanAsync();
        _pick.Clicked += async (_, _) => await PickWgtAsync();
        _install.Clicked += async (_, _) => await RunInstallAsync(forceReplace: false);
        _replace.Clicked += async (_, _) => await ConfirmAndReplaceAsync();
        _tvPicker.SelectedIndexChanged += (_, _) => RememberTv();

        var updateCard = new Border
        {
            Stroke = Color.FromArgb("#202631"),
            StrokeThickness = 1,
            BackgroundColor = Color.FromArgb("#0B0D12"),
            Padding = new Thickness(18, 22),
            Content = new VerticalStackLayout
            {
                Spacing = 12,
                Children =
                {
                    new Label
                    {
                        Text = "🚀",
                        FontSize = 42,
                        HorizontalTextAlignment = TextAlignment.Center
                    },
                    _headline,
                    _updateInfo,
                    _progress,
                    _install,
                    _replace,
                    _status
                }
            }
        };

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(22, 28),
                Spacing = 16,
                Children =
                {
                    new Label
                    {
                        Text = "PrimeTV Updater",
                        FontSize = 30,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Colors.White
                    },
                    new Label
                    {
                        Text = "Phone → Samsung TV",
                        FontSize = 14,
                        TextColor = Color.FromArgb("#8294AA")
                    },
                    updateCard,
                    new Label
                    {
                        Text = "TV",
                        FontSize = 13,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Color.FromArgb("#8294AA")
                    },
                    _tvPicker,
                    _manualIp,
                    _scan,
                    new BoxView { HeightRequest = 1, Color = Color.FromArgb("#202631") },
                    new Label
                    {
                        Text = "Local update package",
                        FontSize = 13,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Color.FromArgb("#8294AA")
                    },
                    _pick,
                    _selected,
                    new Label
                    {
                        Text = "After this updater becomes the signer for PrimeTV, later WGT updates use the same author certificate and install as normal updates.",
                        FontSize = 12,
                        TextColor = Color.FromArgb("#6F8198")
                    }
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var saved = Preferences.Default.Get(SavedTvKey, "");
        if (!string.IsNullOrWhiteSpace(saved))
            _manualIp.Text = saved;
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
            SetStatus("Finding Samsung TV…");
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

            SetStatus(_ips.Count > 0
                ? $"TV ready • {_ips.Count} found"
                : "TV not found automatically. Enter its IP below.");
        }
        catch (Exception ex)
        {
            SetStatus("TV scan failed: " + ex.Message);
        }
        finally
        {
            _busy = false;
            SetButtons(true);
        }
    }

    private async Task PickWgtAsync()
    {
        try
        {
            var result = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "Choose PrimeTV update (.wgt)"
            });
            if (result is null) return;
            if (!result.FileName.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("That is not a .wgt package.");
                return;
            }

            var dest = Path.Combine(FileSystem.CacheDirectory, "PrimeTV-selected.wgt");
            await using var input = await result.OpenReadAsync();
            await using var output = File.Create(dest);
            await input.CopyToAsync(output);

            _wgtPath = dest;
            _selectedName = result.FileName;
            _selected.Text = "Ready: " + result.FileName;
            _headline.Text = "Update Required";
            _updateInfo.Text = "New PrimeTV package ready. Tap Update Now to install it on the TV.";
            _replace.IsVisible = false;
            _progress.Progress = 0;
            SetStatus("Update package ready.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not open WGT: " + ex.Message);
        }
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
        if (ip is not null)
            Preferences.Default.Set(SavedTvKey, ip);
    }

    private async Task RunInstallAsync(bool forceReplace)
    {
        if (_busy) return;
        if (string.IsNullOrWhiteSpace(_wgtPath) || !File.Exists(_wgtPath))
        {
            await PickWgtAsync();
            if (string.IsNullOrWhiteSpace(_wgtPath) || !File.Exists(_wgtPath))
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
        _replace.IsVisible = false;
        _progress.Progress = 0.08;

        try
        {
            NetworkDevice? device = null;
            var idx = _ips.IndexOf(tvIp);
            if (idx >= 0 && idx < _devices.Count)
                device = _devices[idx];

            var guardResult = InstallGuards.Evaluate(device,
                new InstallGuardOptions
                {
                    LocalIps = SafeLocalIps(),
                    ConfiguredLocalIp = NetworkInfo.GetLocalIPv4()
                },
                _networkService);

            foreach (var guard in guardResult.Guards)
            {
                var go = await DisplayAlert(
                    guard.DefaultTitle,
                    guard.DefaultMessageWithDetail,
                    "Continue",
                    "Stop");
                if (!go) return;
            }

            tvIp = guardResult.CorrectedTvIp ?? tvIp;
            _progress.Progress = 0.24;

            if (forceReplace)
            {
                _headline.Text = "Preparing Clean Update";
                SetStatus("Removing the old PrimeTV package…");
                await RemoveExistingPrimeTvAsync(tvIp, _wgtPath);
                _progress.Progress = 0.42;
            }

            SetStatus("Preparing PrimeTV signing certificate…");
            var needsPartner = WgtPrivileges.RequiresPartner(_wgtPath);
            var cert = await _certProvisioner.ProvisionAsync(tvIp, needsPartner, SetStatus);
            _progress.Progress = 0.62;

            _headline.Text = "Updating…";
            SetStatus("Installing PrimeTV on " + tvIp + "…");
            await _installer.InstallAsync(tvIp, _wgtPath, cert, SetStatus);

            _progress.Progress = 1.0;
            _headline.Text = "Update Complete";
            _updateInfo.Text = (_selectedName ?? "PrimeTV") + " is installed on the TV.";
            _install.Text = "Installed";
            SetStatus("✓ PrimeTV updated successfully.");
        }
        catch (Exception ex)
        {
            var message = ex.Message ?? "Unknown install error";
            if (IsAuthorMismatch(message))
            {
                _headline.Text = "One-Time Migration Required";
                _updateInfo.Text = "The PrimeTV already on this TV was signed by a different author certificate. The updater can remove that old copy, wait for the TV to finish removing it, and install the selected build with the updater certificate.";
                _replace.IsVisible = true;
                SetStatus("Author certificate mismatch [118, -11]. Tap Replace Existing PrimeTV.");
            }
            else
            {
                _headline.Text = "Update Failed";
                SetStatus(message);
            }
        }
        finally
        {
            _busy = false;
            SetButtons(true);
        }
    }

    private async Task ConfirmAndReplaceAsync()
    {
        var go = await DisplayAlert(
            "Replace existing PrimeTV?",
            "This one-time migration removes the current PrimeTV app from the TV and installs the selected build again. Local PrimeTV settings/history stored by the old installation may be reset. Continue?",
            "Replace",
            "Cancel");
        if (go)
            await RunInstallAsync(forceReplace: true);
    }

    private async Task RemoveExistingPrimeTvAsync(string tvIp, string wgtPath)
    {
        var (appId, packageId) = await WgtManifest.ReadIdsAsync(wgtPath);
        if (string.IsNullOrWhiteSpace(packageId))
            throw new InvalidOperationException("Could not read the PrimeTV package id from config.xml.");

        var result = await _sdb.UninstallAsync(tvIp, packageId!);
        var raw = ((result.Output ?? "") + "\n" + (result.Error ?? "")).Trim();
        if (result.ExitCode != 0 && !raw.Contains("failed[132]", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("The TV refused to remove the old PrimeTV package: " + raw);

        // Samsung can acknowledge vd_appuninstall before the package database is fully updated.
        // Do not immediately reinstall: that race is what can leave [118, -11] author mismatch.
        for (var attempt = 0; attempt < 24; attempt++)
        {
            await Task.Delay(attempt == 0 ? 1200 : 700);
            try
            {
                var listed = await _sdb.AppsAsync(tvIp);
                var text = listed.Output ?? string.Empty;
                if (text.Contains("Could not retrieve app list", StringComparison.OrdinalIgnoreCase))
                    continue;

                var apps = TizenInstalledApps.Parse(text).ToList();
                var stillInstalled = apps.Any(a =>
                    a.TizenId.Equals(packageId, StringComparison.OrdinalIgnoreCase) ||
                    (!string.IsNullOrWhiteSpace(appId) &&
                     a.AppId?.Equals(appId, StringComparison.OrdinalIgnoreCase) == true));

                if (!stillInstalled && !text.Contains(packageId!, StringComparison.OrdinalIgnoreCase))
                {
                    await _sdb.DisconnectAsync(tvIp);
                    await Task.Delay(900);
                    return;
                }
            }
            catch
            {
                // Keep polling; a reconnect during uninstall completion is normal on some TVs.
            }
        }

        throw new InvalidOperationException(
            "The TV has not finished removing the old PrimeTV package. Wait a few seconds and tap Replace Existing PrimeTV again.");
    }

    private static bool IsAuthorMismatch(string text) =>
        text.Contains("Author certificate", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("different certificate", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("[118, -11]", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("118, -11", StringComparison.OrdinalIgnoreCase);

    private List<string> SafeLocalIps()
    {
        try
        {
            return _networkService.GetRelevantLocalIPs().Select(ip => ip.ToString()).ToList();
        }
        catch
        {
            return new List<string>();
        }
    }

    private void SetButtons(bool enabled)
    {
        _scan.IsEnabled = enabled;
        _pick.IsEnabled = enabled;
        _install.IsEnabled = enabled;
        if (_replace.IsVisible)
            _replace.IsEnabled = enabled;
    }
}
