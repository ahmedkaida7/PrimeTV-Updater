using System.Security.Cryptography;
using System.Text.Json;
using Apps2Samsung.Certificate;
using Apps2Samsung.Interfaces;
using Apps2Samsung.Models;
using Apps2Samsung.Packaging;
using Apps2Samsung.Services;
using Apps2Samsung.Mobile.Services;
using Microsoft.Maui.Storage;
using Microsoft.Maui.ApplicationModel;

namespace Apps2Samsung.Mobile.Pages;

public sealed class PrimeTvUpdaterPage : ContentPage
{
    private const string BundledManifest = "primetv-update.json";
    private const string DefaultManifestUrl = "";
    private const string SavedTvKey = "primetv.updater.tv";
    private const string SavedManifestKey = "primetv.updater.manifest";
    private const string LastInstalledVersionKey = "primetv.updater.lastVersion";

    private readonly INetworkService _networkService;
    private readonly CertificateProvisioner _certProvisioner;
    private readonly WgtInstaller _installer;
    private readonly HttpClient _http;

    private readonly Picker _tvPicker = new() { Title = "Samsung TV" };
    private readonly Label _version = new() { FontSize = 18 };
    private readonly Label _status = new() { Text = "Ready", FontSize = 15 };
    private readonly Label _notes = new() { FontSize = 13, Opacity = 0.78 };
    private readonly ProgressBar _progress = new() { Progress = 0 };
    private readonly Button _scan = new() { Text = "Find TV" };
    private readonly Button _update = new() { Text = "Update TV", FontAttributes = FontAttributes.Bold };
    private readonly Button _rollback = new() { Text = "Rollback" };
    private readonly Entry _manualIp = new() { Placeholder = "TV IP (optional)" };
    private readonly Entry _manifestUrl = new() { Placeholder = "Update manifest URL (optional)" };
    private readonly Switch _autoInstall = new();

    private readonly List<NetworkDevice?> _devices = new();
    private readonly List<string> _ips = new();
    private PrimeTvManifest? _manifest;
    private bool _busy;

    public PrimeTvUpdaterPage(INetworkService networkService, CertificateProvisioner certProvisioner,
        WgtInstaller installer, HttpClient http)
    {
        _networkService = networkService;
        _certProvisioner = certProvisioner;
        _installer = installer;
        _http = http;

        Title = "PrimeTV Updater";
        BackgroundColor = Color.FromArgb("#08111F");

        _scan.Clicked += async (_, _) => await ScanAsync();
        _update.Clicked += async (_, _) => await InstallLatestAsync(false);
        _rollback.Clicked += async (_, _) => await InstallLatestAsync(true);
        _tvPicker.SelectedIndexChanged += (_, _) => RememberSelectedTv();
        _manifestUrl.Completed += (_, _) => SaveManifestUrl();

        var title = new Label
        {
            Text = "PrimeTV Updater",
            FontSize = 30,
            FontAttributes = FontAttributes.Bold,
            TextColor = Colors.White
        };
        var subtitle = new Label
        {
            Text = "One tap: verify → sign → install on your Samsung TV",
            FontSize = 14,
            TextColor = Color.FromArgb("#9DB4D0")
        };
        var autoRow = new HorizontalStackLayout
        {
            Spacing = 12,
            Children = { new Label { Text = "Auto-install when updater opens", VerticalOptions = LayoutOptions.Center }, _autoInstall }
        };

        Content = new ScrollView
        {
            Content = new VerticalStackLayout
            {
                Padding = new Thickness(22, 34),
                Spacing = 14,
                Children =
                {
                    title, subtitle,
                    new BoxView { HeightRequest = 1, Color = Color.FromArgb("#20314A") },
                    _version, _notes,
                    _tvPicker, _manualIp, _scan,
                    _manifestUrl, autoRow,
                    _update, _rollback,
                    _progress, _status,
                    new Label
                    {
                        Text = "The first install may ask for Samsung sign-in/certificate provisioning. After that the certificate is reused for the same TV.",
                        FontSize = 12,
                        TextColor = Color.FromArgb("#8294AA")
                    }
                }
            }
        };
    }

    protected override async void OnAppearing()
    {
        base.OnAppearing();
        _autoInstall.IsToggled = Preferences.Default.Get("primetv.updater.auto", false);
        _autoInstall.Toggled += (_, e) => Preferences.Default.Set("primetv.updater.auto", e.Value);
        _manifestUrl.Text = Preferences.Default.Get(SavedManifestKey, DefaultManifestUrl);
        await LoadManifestAsync();
        await ScanAsync();
        var lastInstalled = Preferences.Default.Get(LastInstalledVersionKey, "0.0.0");
        if (_autoInstall.IsToggled && ResolveTvIp() is not null && IsStrictlyNewer(_manifest?.Version, lastInstalled))
            await InstallLatestAsync(false);
    }

    private void SetStatus(string value)
    {
        MainThread.BeginInvokeOnMainThread(() => _status.Text = value);
    }

    private async Task LoadManifestAsync()
    {
        PrimeTvManifest? embedded = null;
        await using (var stream = await FileSystem.OpenAppPackageFileAsync(BundledManifest))
            embedded = await JsonSerializer.DeserializeAsync<PrimeTvManifest>(stream, JsonOptions);

        var url = (_manifestUrl.Text ?? "").Trim();
        if (!string.IsNullOrWhiteSpace(url))
        {
            try
            {
                SetStatus("Checking PrimeTV update feed…");
                using var response = await _http.GetAsync(url);
                response.EnsureSuccessStatusCode();
                await using var s = await response.Content.ReadAsStreamAsync();
                var remote = await JsonSerializer.DeserializeAsync<PrimeTvManifest>(s, JsonOptions);
                if (remote is not null && IsNewerOrEqual(remote.Version, embedded?.Version))
                    embedded = remote;
            }
            catch (Exception ex)
            {
                SetStatus("Update feed unavailable; using bundled PrimeTV. " + ex.Message);
            }
        }

        _manifest = embedded ?? throw new InvalidOperationException("PrimeTV manifest is missing.");
        _version.Text = $"Available: {_manifest.DisplayName}  •  TV package {_manifest.Version}";
        _notes.Text = _manifest.Notes ?? "";
        var last = Preferences.Default.Get(LastInstalledVersionKey, "not installed by this updater yet");
        _status.Text = $"Last installed by phone: {last}";
    }

    private async Task ScanAsync()
    {
        if (_busy) return;
        _busy = true;
        _scan.IsEnabled = false;
        try
        {
            SetStatus("Scanning local network for Samsung TVs…");
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
            if (_ips.Count == 0 && !string.IsNullOrWhiteSpace(saved)) _manualIp.Text = saved;

            SetStatus(_ips.Count == 0 ? "No TV discovered. Enter its IP manually." : $"Found {_ips.Count} TV(s). Ready.");
        }
        catch (Exception ex)
        {
            SetStatus("TV scan failed: " + ex.Message);
        }
        finally
        {
            _scan.IsEnabled = true;
            _busy = false;
        }
    }

    private string? ResolveTvIp()
    {
        if (_tvPicker.SelectedIndex >= 0 && _tvPicker.SelectedIndex < _ips.Count)
            return _ips[_tvPicker.SelectedIndex];
        var manual = (_manualIp.Text ?? "").Trim();
        return System.Net.IPAddress.TryParse(manual, out _) ? manual : null;
    }

    private void RememberSelectedTv()
    {
        var ip = ResolveTvIp();
        if (ip is not null) Preferences.Default.Set(SavedTvKey, ip);
    }

    private void SaveManifestUrl()
    {
        Preferences.Default.Set(SavedManifestKey, (_manifestUrl.Text ?? "").Trim());
        _ = LoadManifestAsync();
    }

    private async Task InstallLatestAsync(bool rollback)
    {
        if (_busy) return;
        var tvIp = ResolveTvIp();
        if (tvIp is null)
        {
            SetStatus("Select a TV or enter its IP first.");
            return;
        }
        RememberSelectedTv();
        if (_manifest is null) await LoadManifestAsync();
        if (_manifest is null) return;

        _busy = true;
        _update.IsEnabled = _rollback.IsEnabled = _scan.IsEnabled = false;
        _progress.Progress = 0.05;
        string? staged = null;
        try
        {
            if (rollback)
            {
                staged = PreviousWgtPath;
                if (!File.Exists(staged))
                {
                    SetStatus("No previous PrimeTV package has been saved on this phone yet.");
                    return;
                }
                SetStatus("Preparing previous PrimeTV package…");
            }
            else
            {
                staged = await StageLatestWgtAsync(_manifest);
            }

            _progress.Progress = 0.35;
            NetworkDevice? device = null;
            var foundIndex = _ips.IndexOf(tvIp);
            if (foundIndex >= 0 && foundIndex < _devices.Count) device = _devices[foundIndex];

            var guardResult = InstallGuards.Evaluate(device,
                new InstallGuardOptions
                {
                    LocalIps = LocalIps(),
                    ConfiguredLocalIp = NetworkInfo.GetLocalIPv4()
                },
                _networkService);

            foreach (var guard in guardResult.Guards)
            {
                var go = await DisplayAlert(guard.DefaultTitle, guard.DefaultMessageWithDetail, "Continue", "Stop");
                if (!go) return;
            }
            tvIp = guardResult.CorrectedTvIp ?? tvIp;

            SetStatus("Preparing signing certificate…");
            var needsPartner = WgtPrivileges.RequiresPartner(staged);
            var cert = await _certProvisioner.ProvisionAsync(tvIp, needsPartner, SetStatus);
            _progress.Progress = 0.60;

            SetStatus("Installing PrimeTV on " + tvIp + "…");
            await _installer.InstallAsync(tvIp, staged, cert, SetStatus);
            _progress.Progress = 1.0;

            var installedVersion = rollback ? "rollback" : _manifest.Version;
            Preferences.Default.Set(LastInstalledVersionKey, installedVersion);
            SetStatus("✓ PrimeTV installed. You can open it on the TV now.");
        }
        catch (Exception ex)
        {
            SetStatus("Install failed: " + ex.Message);
        }
        finally
        {
            _busy = false;
            _update.IsEnabled = _rollback.IsEnabled = _scan.IsEnabled = true;
        }
    }

    private async Task<string> StageLatestWgtAsync(PrimeTvManifest manifest)
    {
        var destination = CurrentWgtPath;
        var temp = destination + ".download";
        if (File.Exists(temp)) File.Delete(temp);

        if (!string.IsNullOrWhiteSpace(manifest.WgtUrl))
        {
            SetStatus("Downloading " + manifest.DisplayName + "…");
            using var response = await _http.GetAsync(manifest.WgtUrl, HttpCompletionOption.ResponseHeadersRead);
            response.EnsureSuccessStatusCode();
            await using var input = await response.Content.ReadAsStreamAsync();
            await using var output = File.Create(temp);
            await input.CopyToAsync(output);
        }
        else
        {
            SetStatus("Loading bundled PrimeTV package…");
            await using var input = await FileSystem.OpenAppPackageFileAsync(manifest.WgtFile);
            await using var output = File.Create(temp);
            await input.CopyToAsync(output);
        }

        var actual = await Sha256Async(temp);
        if (!string.Equals(actual, manifest.Sha256, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(temp);
            throw new InvalidDataException($"SHA-256 mismatch. Expected {manifest.Sha256}, got {actual}.");
        }

        if (File.Exists(destination) && !FilesEqual(temp, destination))
            File.Copy(destination, PreviousWgtPath, overwrite: true);

        if (File.Exists(destination)) File.Delete(destination);
        File.Move(temp, destination);
        return destination;
    }

    private static bool FilesEqual(string a, string b)
    {
        var fa = new FileInfo(a); var fb = new FileInfo(b);
        if (!fa.Exists || !fb.Exists || fa.Length != fb.Length) return false;
        using var sa = File.OpenRead(a); using var sb = File.OpenRead(b);
        Span<byte> ba = stackalloc byte[8192]; Span<byte> bb = stackalloc byte[8192];
        while (true)
        {
            var ra = sa.Read(ba); var rb = sb.Read(bb);
            if (ra != rb) return false;
            if (ra == 0) return true;
            if (!ba[..ra].SequenceEqual(bb[..rb])) return false;
        }
    }

    private List<string> LocalIps()
    {
        try { return _networkService.GetRelevantLocalIPs().Select(ip => ip.ToString()).ToList(); }
        catch { return new List<string>(); }
    }

    private static async Task<string> Sha256Async(string path)
    {
        await using var stream = File.OpenRead(path);
        var hash = await SHA256.HashDataAsync(stream);
        return Convert.ToHexString(hash).ToLowerInvariant();
    }

    private static bool IsNewerOrEqual(string? a, string? b)
    {
        if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb)) return va >= vb;
        return true;
    }

    private static bool IsStrictlyNewer(string? a, string? b)
    {
        if (Version.TryParse(a, out var va) && Version.TryParse(b, out var vb)) return va > vb;
        return !string.Equals(a, b, StringComparison.OrdinalIgnoreCase);
    }

    private static readonly JsonSerializerOptions JsonOptions = new() { PropertyNameCaseInsensitive = true };
    private static string CurrentWgtPath => Path.Combine(FileSystem.AppDataDirectory, "PrimeTV-current.wgt");
    private static string PreviousWgtPath => Path.Combine(FileSystem.AppDataDirectory, "PrimeTV-previous.wgt");

    private sealed class PrimeTvManifest
    {
        public string Channel { get; set; } = "stable";
        public string Version { get; set; } = "0.0.0";
        public string DisplayName { get; set; } = "PrimeTV";
        public string WgtFile { get; set; } = "PrimeTV.wgt";
        public string WgtUrl { get; set; } = "";
        public string Sha256 { get; set; } = "";
        public string PackageId { get; set; } = "PrimeTV003";
        public string ApplicationId { get; set; } = "PrimeTV003.PrimeTV";
        public string? Notes { get; set; }
    }
}
