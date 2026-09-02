#!/usr/bin/env python3
from pathlib import Path
import shutil, sys, re

root = Path(sys.argv[1] if len(sys.argv) > 1 else "Apps2Samsung")
overlay = Path(__file__).resolve().parent / "overlay"
mobile = root / "Apps2Samsung.Mobile"
if not mobile.exists():
    raise SystemExit(f"Apps2Samsung.Mobile not found under {root}")

# Copy overlay files, preserving relative paths.
for src in overlay.rglob('*'):
    if src.is_file():
        rel = src.relative_to(overlay)
        dst = root / rel
        dst.parent.mkdir(parents=True, exist_ok=True)
        shutil.copy2(src, dst)

# Register PrimeTvUpdaterPage in DI.
p = mobile / "MauiProgram.cs"
s = p.read_text(encoding='utf-8-sig')
needle = "builder.Services.AddTransient<InstallerPage>();"
replacement = needle + "\n        builder.Services.AddTransient<PrimeTvUpdaterPage>();"
if "AddTransient<PrimeTvUpdaterPage>" not in s:
    if needle not in s: raise SystemExit("MauiProgram registration anchor not found")
    s = s.replace(needle, replacement)
p.write_text(s, encoding='utf-8')

# Make updater the first page.
p = mobile / "App.xaml.cs"
s = p.read_text(encoding='utf-8-sig')
s2 = re.sub(r"services\.GetRequiredService<Pages\.InstallerPage>\(\)",
            "services.GetRequiredService<Pages.PrimeTvUpdaterPage>()", s)
if s2 == s: raise SystemExit("App.xaml.cs first-page anchor not found")
p.write_text(s2, encoding='utf-8')

# Rename Android app. Keep the upstream namespace so all shared Core code stays untouched.
p = mobile / "Apps2Samsung.Mobile.csproj"
s = p.read_text(encoding='utf-8-sig')
s = re.sub(r"<ApplicationTitle>.*?</ApplicationTitle>", "<ApplicationTitle>PrimeTV Updater</ApplicationTitle>", s)
s = re.sub(r"<ApplicationId>.*?</ApplicationId>", "<ApplicationId>com.primetv.updater</ApplicationId>", s)
s = re.sub(r"<ApplicationDisplayVersion>.*?</ApplicationDisplayVersion>", "<ApplicationDisplayVersion>0.1.0</ApplicationDisplayVersion>", s)
s = re.sub(r"<ApplicationVersion>.*?</ApplicationVersion>", "<ApplicationVersion>1</ApplicationVersion>", s)
# Use PrimeTV artwork as launcher icon.
s = re.sub(r'<MauiIcon Include="Resources\\AppIcon\\appicon\.png"[^>]*/>',
           '<MauiIcon Include="Resources\\AppIcon\\primetv_updater.png" Color="#08111F" />', s)
p.write_text(s, encoding='utf-8')

print("PrimeTV Updater overlay applied successfully.")
