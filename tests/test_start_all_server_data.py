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
