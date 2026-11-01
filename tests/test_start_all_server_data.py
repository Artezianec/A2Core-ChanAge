import io
import sys
import tarfile
from pathlib import Path

sys.path.insert(0, str(Path(__file__).resolve().parents[1] / 'tools'))

import start_all_windows  # noqa: E402


def _archive(path: Path) -> None:
    files = {
        'bootstrap/keys/app-key.json': b'{}',
        'bootstrap/policy/app-policy.json': b'{}',
        'ncguard/fixture.json': b'{"rules": []}',
        'game-catalog-v2/manifest.json': b'{}',
        'terrain/1.bin': b'x',
    }
    with tarfile.open(path, 'w:gz') as handle:
        for name, data in files.items():
            info = tarfile.TarInfo(name)
            info.size = len(data)
            handle.addfile(info, io.BytesIO(data))


def test_install_server_data_places_folders(tmp_path):
    archive = tmp_path / 'aion-server-data.tar.gz'
    _archive(archive)
    local = tmp_path / 'local'
    start_all_windows.install_server_data(archive, local)
    assert (local / 'bootstrap-fixtures/rsa-candidate-20261009/app-key.json').is_file()
    assert (local / 'bootstrap-fixtures/policy-login-route-20261009-1230/app-policy.json').is_file()
    assert (local / 'bootstrap-fixtures/policy-lime-20261014/app-policy.json').is_file()
    assert (local / 'ncguard/fixture.json').is_file()
    assert (local / 'game-catalog-v2/manifest.json').is_file()
    assert (local / 'terrain/1.bin').is_file()
    assert not (local / '.server-data-extract').exists()


def test_check_fixtures_hints_release_when_archive_missing(tmp_path, monkeypatch, capsys):
    monkeypatch.setattr(start_all_windows, 'DEFAULT_POLICY', tmp_path / 'p')
    monkeypatch.setattr(start_all_windows, 'KEY_FIXTURES', tmp_path / 'k')
    monkeypatch.setattr(start_all_windows, 'find_data_archive', lambda: None)
    assert start_all_windows.check_fixtures() == 1
    assert start_all_windows.RELEASE_URL in capsys.readouterr().out


def test_check_node_reports_missing_npm(monkeypatch, capsys):
    monkeypatch.delenv('AION_SKIP_WEB', raising=False)
    monkeypatch.setattr(start_all_windows.shutil, 'which', lambda tool: None if tool == 'npm' else '/bin/' + tool)
    assert start_all_windows.check_node() == 1
    out = capsys.readouterr().out
    assert 'npm' in out and 'https://nodejs.org/' in out and 'new terminal' in out


def test_check_node_ok_or_skipped(monkeypatch):
    monkeypatch.setattr(start_all_windows.shutil, 'which', lambda tool: '/bin/' + tool)
    assert start_all_windows.check_node() == 0
    monkeypatch.setattr(start_all_windows.shutil, 'which', lambda tool: None)
    monkeypatch.setenv('AION_SKIP_WEB', '1')
    assert start_all_windows.check_node() == 0


def test_check_dotnet_missing(monkeypatch, capsys):
    def boom(*a, **k):
        raise FileNotFoundError
    monkeypatch.setattr(start_all_windows.subprocess, 'run', boom)
    assert start_all_windows.check_dotnet() == 1
    assert 'dotnet.microsoft.com' in capsys.readouterr().out
