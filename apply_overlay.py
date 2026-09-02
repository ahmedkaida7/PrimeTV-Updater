#!/usr/bin/env python3
from pathlib import Path
import shutil, sys, re

root = Path(sys.argv[1] if len(sys.argv) > 1 else "Apps2Samsung")
overlay = Path(__file__).resolve().parent / "overlay"
mobile = root / "Apps2Samsung.Mobile"
if not mobile.exists():
    raise SystemExit(f"Apps2Samsung.Mobile not found under {root}")

# Apply the PrimeTV-specific files on top of the pinned Apps2Samsung engine.
for src in overlay.rglob('*'):
    if src.is_file():
        rel = src.relative_to(overlay)
        dst = root / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)

# Register the updater page in DI.
p = mobile / "MauiProgram.cs"
s = p.read_text(encoding='utf-8-sig')
needle = "builder.Services.AddTransient<InstallerPage>();"
replacement = needle + "\n        builder.Services.AddTransient<PrimeTvUpdaterPage>();"
if "AddTransient<PrimeTvUpdaterPage>" not in s:
    if needle not in s:
        raise SystemExit("MauiProgram registration anchor not found")
    s = s.replace(needle, replacement)
p.write_text(s, encoding='utf-8')

# Make PrimeTV Updater the first page.
p = mobile / "App.xaml.cs"
s = p.read_text(encoding='utf-8-sig')
s2 = re.sub(r"services\.GetRequiredService<Pages\.InstallerPage>\(\)",
            "services.GetRequiredService<Pages.PrimeTvUpdaterPage>()", s)
if s2 == s:
    raise SystemExit("App.xaml.cs first-page anchor not found")
p.write_text(s2, encoding='utf-8')

# Rename/version the Android app.
p = mobile / "Apps2Samsung.Mobile.csproj"
s = p.read_text(encoding='utf-8-sig')
s = re.sub(r"<ApplicationTitle>.*?</ApplicationTitle>", "<ApplicationTitle>PrimeTV Updater</ApplicationTitle>", s)
s = re.sub(r"<ApplicationId>.*?</ApplicationId>", "<ApplicationId>com.primetv.updater</ApplicationId>", s)
s = re.sub(r"<ApplicationDisplayVersion>.*?</ApplicationDisplayVersion>", "<ApplicationDisplayVersion>0.2.0</ApplicationDisplayVersion>", s)
s = re.sub(r"<ApplicationVersion>.*?</ApplicationVersion>", "<ApplicationVersion>2</ApplicationVersion>", s)
p.write_text(s, encoding='utf-8')

# Apps2Samsung already knows how to recover from a Tizen author-certificate mismatch by
# uninstalling and retrying. On some 2024/2025 Samsung TVs vd_appuninstall returns before the
# package database is actually clear; an immediate reinstall can therefore hit the exact same
# [118, -11] Author certificate mismatch. Wait until vd_applist no longer reports the package,
# then reconnect before retrying the install.
p = mobile / "Services" / "WgtInstaller.cs"
s = p.read_text(encoding='utf-8-sig')
old = '''\t\t\tprogress?.Invoke("Install failed — removing the old copy and retrying…");
\t\t\ttry { await _sdb.UninstallAsync(tvIp, packageId!); } catch { /* best-effort */ }

\t\t\tvar retry = await _sdb.InstallAsync(tvIp, wgtPath, sdkToolPath);'''
new = '''\t\t\tprogress?.Invoke("Install failed — removing the old copy and waiting for the TV…");
\t\t\tvar removed = await PrimeTvRemoveAndWaitAsync(tvIp, packageId!, progress);
\t\t\tif (!removed)
\t\t\t\tthrow new InvalidOperationException(
\t\t\t\t\t"The TV did not finish removing the old app. Wait a few seconds and retry the update.");

\t\t\tvar retry = await _sdb.InstallAsync(tvIp, wgtPath, sdkToolPath);'''
if old not in s:
    raise SystemExit("WgtInstaller recovery anchor not found")
s = s.replace(old, new, 1)

anchor = '''\n\tprivate static string Detail(string error, string output) =>'''
helper = r'''

	private async Task<bool> PrimeTvRemoveAndWaitAsync(
		string tvIp, string packageId, Action<string>? progress)
	{
		try
		{
			await _sdb.UninstallAsync(tvIp, packageId);
		}
		catch
		{
			// The package may already be disappearing; verification below is authoritative.
		}

		for (var attempt = 0; attempt < 24; attempt++)
		{
			await Task.Delay(attempt == 0 ? 1200 : 700);
			try
			{
				var listed = await _sdb.AppsAsync(tvIp);
				var text = listed.Output ?? string.Empty;
				if (string.IsNullOrWhiteSpace(text) ||
					text.Contains("Could not retrieve app list", StringComparison.OrdinalIgnoreCase))
					continue;

				var parsed = TizenInstalledApps.Parse(text).ToList();
				var stillInstalled = parsed.Any(a =>
					a.TizenId.Equals(packageId, StringComparison.OrdinalIgnoreCase) ||
					(a.AppId?.StartsWith(packageId + ".", StringComparison.OrdinalIgnoreCase) ?? false));

				if (!stillInstalled && !text.Contains(packageId, StringComparison.OrdinalIgnoreCase))
				{
					progress?.Invoke("Old package removed. Reconnecting…");
					try { await _sdb.DisconnectAsync(tvIp); } catch { }
					await Task.Delay(900);
					return true;
				}
			}
			catch
			{
				// Samsung may reset SDB while finalizing an uninstall. Poll again on a fresh connection.
			}
		}
		return false;
	}
'''
if anchor not in s:
    raise SystemExit("WgtInstaller helper anchor not found")
s = s.replace(anchor, helper + anchor, 1)
p.write_text(s, encoding='utf-8')

print("PrimeTV Updater overlay applied successfully.")
