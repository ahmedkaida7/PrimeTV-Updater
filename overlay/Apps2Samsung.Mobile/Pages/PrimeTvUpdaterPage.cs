using Apps2Samsung.Backup;
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
    private const string SavedWgtNameKey = "primetv.updater.wgtname";
    private static string SavedWgtPath => Path.Combine(FileSystem.AppDataDirectory, "PrimeTV-selected.wgt");
    private static string CertStorePath => new MobileAppConfig().CertificateStorePath;

    private readonly INetworkService _networkService;
    private readonly CertificateProvisioner _certProvisioner;
    private readonly WgtInstaller _installer;

    private readonly Picker _tvPicker = new() { Title = "Samsung TV" };
    private readonly Entry _manualIp = new() { Placeholder = "TV IP address (optional)" };
    private readonly Label _headline = new()
    {
        Text = "PrimeTV Update",
        FontSize = 24,
        FontAttributes = FontAttributes.Bold,
        HorizontalTextAlignment = TextAlignment.Center,
        TextColor = Colors.White
    };
    private readonly Label _updateInfo = new()
    {
        Text = "Choose the PrimeTV WGT, then tap Update TV.",
        FontSize = 14,
        HorizontalTextAlignment = TextAlignment.Center,
        TextColor = Color.FromArgb("#A8B8CD")
    };
    private readonly Button _scan = new() { Text = "Find TV" };
    private readonly Button _pick = new() { Text = "Select PrimeTV WGT" };
    private readonly Button _install = new()
    {
        Text = "Update TV",
        FontAttributes = FontAttributes.Bold,
        BackgroundColor = Color.FromArgb("#38546E"),
        TextColor = Colors.White
    };
    private readonly Button _importBackup = new()
    {
        Text = "Import Apps2Samsung Signing Backup",
        FontAttributes = FontAttributes.Bold,
        BackgroundColor = Color.FromArgb("#6B4D16"),
        TextColor = Colors.White
    };
    private readonly Label _certHelp = new()
    {
        Text = "Only needed if the TV says ‘Author certificate not match’. In the Apps2Samsung app that originally installed PrimeTV: Settings → Backup & Restore → Export Backup. Then import that ZIP here once.",
        FontSize = 12,
        TextColor = Color.FromArgb("#8EA0B7")
    };
    private readonly Label _selected = new()
    {
        Text = "No update package selected",
        FontSize = 13,
        TextColor = Colors.White
    };
    private readonly Label _status = new()
    {
        Text = "Ready",
        FontSize = 14,
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
        WgtInstaller installer)
    {
        _networkService = networkService;
        _certProvisioner = certProvisioner;
        _installer = installer;

        Title = "PrimeTV Updater";
        BackgroundColor = Color.FromArgb("#06101E");

        _scan.Clicked += async (_, _) => await ScanAsync();
        _pick.Clicked += async (_, _) => await PickWgtAsync();
        _install.Clicked += async (_, _) => await RunInstallAsync();
        _importBackup.Clicked += async (_, _) => await ImportSigningBackupAsync();
        _tvPicker.SelectedIndexChanged += (_, _) => RememberTv();

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
                    new Border
                    {
                        Stroke = Color.FromArgb("#20314A"),
                        StrokeThickness = 1,
                        BackgroundColor = Color.FromArgb("#091426"),
                        Padding = new Thickness(18, 20),
                        Content = new VerticalStackLayout
                        {
                            Spacing = 12,
                            Children = { _headline, _updateInfo, _progress, _status }
                        }
                    },
                    _tvPicker,
                    _manualIp,
                    _scan,
                    _pick,
                    _selected,
                    _install,
                    new BoxView { HeightRequest = 1, Color = Color.FromArgb("#20314A") },
                    new Label
                    {
                        Text = "Certificate migration (one time)",
                        FontSize = 14,
                        FontAttributes = FontAttributes.Bold,
                        TextColor = Colors.White
                    },
                    _importBackup,
                    _certHelp,
                    new Label
                    {
                        Text = "PrimeTV Updater v0.2 • Never removes the installed PrimeTV automatically on a certificate mismatch.",
                        FontSize = 11,
                        TextColor = Color.FromArgb("#6F8198")
                    }
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        var savedTv = Preferences.Default.Get(SavedTvKey, "");
        if (!string.IsNullOrWhiteSpace(savedTv))
            _manualIp.Text = savedTv;

        if (File.Exists(SavedWgtPath))
        {
            _wgtPath = SavedWgtPath;
            _selectedName = Preferences.Default.Get(SavedWgtNameKey, "PrimeTV update.wgt");
            _selected.Text = "Selected: " + _selectedName;
        }

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

            SetStatus(_ips.Count > 0 ? $"TV ready • {_ips.Count} found" : "TV not found automatically. Enter its IP.");
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
            var result = await FilePicker.Default.PickAsync(new PickOptions { PickerTitle = "Choose PrimeTV update (.wgt)" });
            if (result is null) return;
            if (!result.FileName.EndsWith(".wgt", StringComparison.OrdinalIgnoreCase))
            {
                SetStatus("That is not a .wgt package.");
                return;
            }

            var temp = SavedWgtPath + ".new";
            if (File.Exists(temp)) File.Delete(temp);
            await using (var input = await result.OpenReadAsync())
            await using (var output = File.Create(temp))
                await input.CopyToAsync(output);

            if (File.Exists(SavedWgtPath)) File.Delete(SavedWgtPath);
            File.Move(temp, SavedWgtPath);
            _wgtPath = SavedWgtPath;
            _selectedName = result.FileName;
            Preferences.Default.Set(SavedWgtNameKey, _selectedName);
            _selected.Text = "Selected: " + _selectedName;
            _progress.Progress = 0;
            _headline.Text = "Update Ready";
            _updateInfo.Text = "The package is saved on this phone. You do not have to select it again after reopening the updater.";
            SetStatus("Ready to update TV.");
        }
        catch (Exception ex)
        {
            SetStatus("Could not open WGT: " + ex.Message);
        }
    }

    private async Task ImportSigningBackupAsync()
    {
        if (_busy) return;
        _busy = true;
        SetButtons(false);
        try
        {
            _headline.Text = "Certificate Migration";
            SetStatus("Choose the backup ZIP exported by Apps2Samsung…");
            var picked = await SafFilePicker.PickAsync("application/zip", "application/octet-stream");
            if (picked is null)
            {
                SetStatus("Backup import cancelled.");
                return;
            }

            BackupImportResult imported;
            using (var stream = File.OpenRead(picked.LocalPath))
                imported = BackupService.Import(stream, CertStorePath);

            DefaultCertificateToImportedProfile();
            _headline.Text = "Signing Certificate Imported";
            _updateInfo.Text = $"Restored {imported.CertificateFilesRestored} certificate file(s). PrimeTV updates can now use the same author identity as the original installer.";
            SetStatus("Certificate ready. Retrying the PrimeTV update…");
        }
        catch (Exception ex)
        {
            _headline.Text = "Certificate Import Failed";
            SetStatus("Could not import Apps2Samsung backup: " + ex.Message);
            return;
        }
        finally
        {
            _busy = false;
            SetButtons(true);
        }

        if (File.Exists(SavedWgtPath) && ResolveTvIp() is not null)
            await RunInstallAsync();
    }

    private static void DefaultCertificateToImportedProfile()
    {
        bool Has(CertificatePrivilegeLevel level) =>
            CertificateProvisioningService.HasUsableAuthorCert(
                Path.Combine(CertStorePath, CertificateProvisioningService.ProfileName(level)));

        var partner = Has(CertificatePrivilegeLevel.Partner);
        var pub = Has(CertificatePrivilegeLevel.Public);
        if (partner && !pub) MobileSettings.CertificatePreference = MobileSettings.CertificatePreferencePartner;
        else if (pub && !partner) MobileSettings.CertificatePreference = MobileSettings.CertificatePreferencePublic;
        else MobileSettings.CertificatePreference = MobileSettings.CertificatePreferenceAuto;
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

    private async Task RunInstallAsync()
    {
        if (_busy) return;
        if (string.IsNullOrWhiteSpace(_wgtPath) || !File.Exists(_wgtPath))
        {
            await PickWgtAsync();
            if (string.IsNullOrWhiteSpace(_wgtPath) || !File.Exists(_wgtPath)) return;
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
        _progress.Progress = 0.08;
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
                },
                _networkService);

            foreach (var guard in guardResult.Guards)
            {
                var go = await DisplayAlert(guard.DefaultTitle, guard.DefaultMessageWithDetail, "Continue", "Stop");
                if (!go) return;
            }

            tvIp = guardResult.CorrectedTvIp ?? tvIp;
            _progress.Progress = 0.28;
            _headline.Text = "Preparing Update";
            SetStatus("Preparing the signing certificate…");
            var needsPartner = WgtPrivileges.RequiresPartner(_wgtPath);
            var cert = await _certProvisioner.ProvisionAsync(tvIp, needsPartner, SetStatus);

            _progress.Progress = 0.62;
            _headline.Text = "Updating TV…";
            SetStatus("Installing " + (_selectedName ?? "PrimeTV") + "…");
            await _installer.InstallAsync(tvIp, _wgtPath, cert, SetStatus);

            _progress.Progress = 1;
            _headline.Text = "Update Complete";
            _updateInfo.Text = (_selectedName ?? "PrimeTV") + " is installed on the TV.";
            SetStatus("✓ PrimeTV updated successfully.");
        }
        catch (Exception ex)
        {
            var message = ex.Message ?? "Unknown install error";
            _progress.Progress = 0.65;
            if (IsAuthorMismatch(message))
            {
                _headline.Text = "Signing Certificate Needed";
                _updateInfo.Text = "The PrimeTV already installed on this TV was signed by the original Apps2Samsung certificate. Import that Apps2Samsung backup once below. PrimeTV Updater will NOT uninstall the working app.";
                SetStatus("Author certificate mismatch [118, -11]. Import Apps2Samsung Signing Backup, then the updater will retry automatically.");
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

    private static bool IsAuthorMismatch(string text) =>
        text.Contains("Author certificate", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("different certificate", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("[118, -11]", StringComparison.OrdinalIgnoreCase) ||
        text.Contains("118, -11", StringComparison.OrdinalIgnoreCase);

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
        _importBackup.IsEnabled = enabled;
    }
}
