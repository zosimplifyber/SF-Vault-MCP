import json
import shutil
import subprocess
import sys
from pathlib import Path

import pytest

ROOT = Path(__file__).resolve().parent.parent
sys.path.insert(0, str(ROOT / "scripts"))
import vault_bridge as vb  # noqa: E402
import build_install_guide  # noqa: E402

from tests.test_vault_bridge import server  # noqa: E402,F401


def _make_zip(tmp_path, name="SF Vault Bridge 1.2.3.zip"):
    z = tmp_path / name
    z.write_bytes(b"PK")
    return z


def test_publish_release_stages_in_downloads_and_cleans_up(server, tmp_path, monkeypatch):  # noqa: F811
    monkeypatch.setenv("USERPROFILE", str(tmp_path / "home"))
    server.replies["publish_release"] = (200, {"name": "SF Vault Bridge 1.2.3.zip", "version": "1.2.3"})
    z = _make_zip(tmp_path)
    out = vb.publish_release(z)
    op, body, _ = server.seen[-1]
    assert op == "publish_release" and body["version"] == "1.2.3"
    assert Path(body["zipPath"]).parent == tmp_path / "home" / "Downloads" / "SF Vault Bridge" / "publish"
    assert not Path(body["zipPath"]).exists()
    assert out["version"] == "1.2.3"


def test_publish_release_needs_version_from_name(tmp_path):
    z = _make_zip(tmp_path, "build.zip")
    with pytest.raises(ValueError):
        vb.publish_release(z)
    with pytest.raises(FileNotFoundError):
        vb.publish_release(tmp_path / "missing.zip")


def test_cli_parses_publish_release():
    args = vb.build_parser().parse_args(["publish-release", "x.zip", "--version", "2.0.0"])
    assert args.command == "publish-release" and args.version == "2.0.0"


def test_install_guide_builds_a_pdf(tmp_path):
    out = tmp_path / "guide.pdf"
    build_install_guide.build(out, "1.0.0")
    assert out.read_bytes().startswith(b"%PDF")


def test_version_file_is_semver():
    import re
    assert re.fullmatch(r"\d+\.\d+\.\d+", (ROOT / "inventor-addin" / "VERSION").read_text().strip())


@pytest.mark.skipif(shutil.which("powershell") is None, reason="needs Windows PowerShell")
def test_install_package_into_temp_target(tmp_path):
    pkg = tmp_path / "pkg"
    bundle = pkg / "SFVaultBridge.bundle"
    (bundle / "Contents" / "tools").mkdir(parents=True)
    (bundle / "PackageContents.xml").write_text(
        '<ApplicationPackage AppVersion="9.9.9"><Components/></ApplicationPackage>', encoding="utf-8")
    (bundle / "Contents" / "SFVaultBridge.Addin.dll").write_bytes(b"x")
    (bundle / "Contents" / "tools" / "SFVaultTools.exe").write_bytes(b"x")
    (bundle / "Contents" / "SFVaultBridge.addin").write_text("<Assembly>{{ASSEMBLY}}</Assembly>", encoding="utf-8")
    (pkg / "team-settings.json").write_text(json.dumps({
        "processValues": ["CNC"], "printMaterials": ["PLA"], "dropboxAppKey": "k", "salesTaxRate": 0.09,
        "recentPartners": ["ignored"]}), encoding="utf-8")
    target = tmp_path / "plugins"
    data = tmp_path / "data"
    data.mkdir()
    (data / "settings.json").write_text(json.dumps({
        "processValues": ["Old"], "recentPartners": ["Acme"], "extra": 1}), encoding="utf-8")
    # An older install: ours only, so it is removed. A foreign file would keep it.
    (target / "SFVaultBridge").mkdir(parents=True)
    (target / "SFVaultBridge" / "SFVaultBridge.Addin.dll").write_bytes(b"old")
    script = ROOT / "inventor-addin" / "Install-Package.ps1"
    r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script),
                        "-PackageDir", str(pkg), "-Target", str(target), "-DataDir", str(data),
                        "-OldAddinsDir", str(tmp_path / "addins"), "-SkipInventorCheck"],
                       capture_output=True, text=True, timeout=120)
    assert r.returncode == 0, r.stdout + r.stderr
    dest = target / "SFVaultBridge.bundle"
    assert (dest / "PackageContents.xml").exists()
    assert not (target / "SFVaultBridge").exists()
    addin = (dest / "Contents" / "SFVaultBridge.addin").read_text(encoding="utf-8-sig")
    assert str(dest / "Contents" / "SFVaultBridge.Addin.dll") in addin and "{{ASSEMBLY}}" not in addin
    settings = json.loads((data / "settings.json").read_text(encoding="utf-8"))
    assert settings["processValues"] == ["CNC"] and settings["dropboxAppKey"] == "k" and settings["salesTaxRate"] == 0.09
    assert settings["recentPartners"] == ["Acme"] and settings["extra"] == 1
    assert (target / "Uninstall SF Vault Bridge.cmd").exists()
    # A second run is clean (replaces the bundle).
    r2 = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script),
                         "-PackageDir", str(pkg), "-Target", str(target), "-DataDir", str(data),
                         "-OldAddinsDir", str(tmp_path / "addins"), "-SkipInventorCheck"],
                        capture_output=True, text=True, timeout=120)
    assert r2.returncode == 0, r2.stdout + r2.stderr


@pytest.mark.skipif(shutil.which("powershell") is None, reason="needs Windows PowerShell")
def test_install_package_keeps_foreign_old_folder(tmp_path):
    pkg = tmp_path / "pkg"
    bundle = pkg / "SFVaultBridge.bundle"
    (bundle / "Contents").mkdir(parents=True)
    (bundle / "PackageContents.xml").write_text('<ApplicationPackage AppVersion="1.0.0"/>', encoding="utf-8")
    (bundle / "Contents" / "SFVaultBridge.addin").write_text("{{ASSEMBLY}}", encoding="utf-8")
    target = tmp_path / "plugins"
    (target / "SFVaultBridge").mkdir(parents=True)
    (target / "SFVaultBridge" / "mine.txt").write_text("keep", encoding="utf-8")
    script = ROOT / "inventor-addin" / "Install-Package.ps1"
    r = subprocess.run(["powershell", "-NoProfile", "-ExecutionPolicy", "Bypass", "-File", str(script),
                        "-PackageDir", str(pkg), "-Target", str(target), "-DataDir", str(tmp_path / "d"),
                        "-OldAddinsDir", str(tmp_path / "a"), "-SkipInventorCheck"],
                       capture_output=True, text=True, timeout=120)
    assert r.returncode == 0, r.stdout + r.stderr
    assert (target / "SFVaultBridge" / "mine.txt").exists()
