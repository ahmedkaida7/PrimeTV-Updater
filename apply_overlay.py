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

# IMPORTANT: the generic Apps2Samsung recovery path can remove an existing app after a
# certificate mismatch. PrimeTV Updater must never do that automatically because it would
# destroy the working installation before the original author certificate is migrated.
p = mobile / "Services" / "WgtInstaller.cs"
s = p.read_text(encoding='utf-8-sig')
needle = '''\t\tbool certMismatch = TizenInstallDiagnostics.IsCertificateMismatch(output);\n\t\tbool outOfSpace = TizenInstallDiagnostics.IsInsufficientSpace(output);'''
replacement = '''\t\tbool certMismatch = TizenInstallDiagnostics.IsCertificateMismatch(output);\n\t\tbool outOfSpace = TizenInstallDiagnostics.IsInsufficientSpace(output);\n\n\t\t// PrimeTV Updater preserves the working app. The user can import the original\n\t\t// Apps2Samsung backup (author.p12 + profile) and retry without uninstalling anything.\n\t\tif (certMismatch)\n\t\t\tthrow new InvalidOperationException(\n\t\t\t\t"Author certificate mismatch [118, -11]. Import the Apps2Samsung signing backup in PrimeTV Updater and retry.");'''
if needle not in s:
    raise SystemExit("WgtInstaller certificate mismatch anchor not found")
s = s.replace(needle, replacement, 1)
p.write_text(s, encoding='utf-8')

print("PrimeTV Updater overlay applied successfully.")
