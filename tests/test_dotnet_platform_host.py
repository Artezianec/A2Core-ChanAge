"""End-to-end check of the .NET host's platform bootstrap (port of the HTTP side of
probe_dev_bootstrap.py): fixture routes, a policy encrypted from plaintext and pointed at the
host's own ports, launch codes in the bootstrap URL, a public bind/advertised address, and 503 for
everything else. Uses synthetic fixtures only. Skipped when the host is not built (dotnet build
server/Aion.Host/Aion.Host.csproj; AION_ARTIFACTS_PATH selects an artifacts build).
"""
import base64
import http.client
import json
import os
import shutil
import socket
import subprocess
import tempfile
import time
import unittest
from pathlib import Path

from tests.aes_reference import cbc_decrypt

ROOT = Path(__file__).resolve().parents[1]
HOST_DLL = (ROOT / os.environ['AION_ARTIFACTS_PATH'] / 'bin/Aion.Host/debug/Aion.Host.dll'
            if os.environ.get('AION_ARTIFACTS_PATH') else ROOT / 'server/Aion.Host/bin/Debug/net10.0/Aion.Host.dll')
AES_KEY = '0123456789abcdef'
CODE = 'AB' * 32  # launch authorizations are upper-case hex (Convert.ToHexString)


def free_port():
    with socket.socket() as probe:
        probe.bind(('127.0.0.1', 0))
        return probe.getsockname()[1]


@unittest.skipUnless(HOST_DLL.exists() and shutil.which('dotnet') and os.name == 'nt', 'Aion.Host is not built')
class DotnetPlatformHostTests(unittest.TestCase):
    network = {}

    def setUp(self):
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        fixtures = Path(temp.name)
        self.key_value = base64.b64encode(os.urandom(256)).decode()
        (fixtures / 'app-key.json').write_text(json.dumps({'value': self.key_value}))
        (fixtures / 'local-test-key.json').write_text(json.dumps({'key': AES_KEY, 'status': 'test'}))
        (fixtures / 'app-maintenance.json').write_text(json.dumps({'value': base64.b64encode(bytes(16)).decode()}))
        session = {'session': 'local-abc', 'session_secret': 's', 'game_account_id': '1', 'login_name': 'l',
                   'user_id': '1', 'user_name': 'u'}
        (fixtures / 'session-response.candidate.json').write_text(json.dumps(session))
        self.session = session
        (fixtures / 'authn-response.candidate.json').write_text(json.dumps({'authn_token': 'local-dev-token'}))
        (fixtures / 'policy.json').write_text(json.dumps({'api_gate': 'http://127.0.0.1:8087/local-api-gate', 'custom_data': {
            'LoginServerIP': '127.0.0.1:1', 'ChatServerURL': 'http://127.0.0.1:2', 'CommunityUIEnable': 'true'}}))
        (fixtures / 'geo.json').write_text('{"geolocation":"NO"}')
        self.port = free_port()
        self.log = fixtures / 'requests.jsonl'
        env = dict(os.environ, Aion__LocalPort=str(free_port()), Aion__Platform__Port=str(self.port),
                   Aion__Platform__FixtureDirectory=str(fixtures),
                   Aion__Platform__PolicyPlaintextFile=str(fixtures / 'policy.json'),
                   Aion__Platform__GeolocationFile=str(fixtures / 'geo.json'), Aion__Platform__RequestLog=str(self.log),
                   Aion__Native__LoginPort=str(free_port()), Aion__Native__Logins='development',
                   Aion__ChatPort=str(free_port()), **self.network)
        env.pop('ConnectionStrings__Aion', None)
        env.pop('ConnectionStrings__Auth', None)
        self.env = env
        self.process = subprocess.Popen(['dotnet', str(HOST_DLL)], cwd=HOST_DLL.parent, env=env,
                                        stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        self.addCleanup(self.stop)
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            try:
                socket.create_connection(('127.0.0.1', self.port), timeout=0.5).close()
                return
            except OSError:
                time.sleep(0.2)
        self.fail('host did not open the platform port')

    def stop(self):
        self.process.terminate()
        self.process.wait(timeout=10)

    def request(self, method, path):
        connection = http.client.HTTPConnection('127.0.0.1', self.port, timeout=5)
        connection.request(method, path, body=b'{}' if method == 'POST' else None)
        response = connection.getresponse()
        body = response.read()
        connection.close()
        return response.status, body

    def policy(self, path='/MobileAppConfigSdk/app/policy'):
        status, body = self.request('GET', path)
        self.assertEqual(status, 200)
        key = AES_KEY.encode()
        return json.loads(cbc_decrypt(key, key, base64.b64decode(json.loads(body)['value'])))

    def test_launch_code_routes(self):
        # /c/<code>/...: the same routes, a policy whose api_gate keeps the code, and authn_token
        # redeeming the code (no account service here, so it is rejected, not the fixture token).
        prefix = '/c/' + CODE
        self.assertEqual(self.request('GET', prefix + '/MobileAppConfigSdk/app/key')[0], 200)
        self.assertEqual(self.policy(prefix + '/MobileAppConfigSdk/app/policy')['api_gate'],
                         f'http://127.0.0.1:{self.port}{prefix}/local-api-gate')
        self.assertEqual(self.policy()['api_gate'], f'http://127.0.0.1:{self.port}/local-api-gate')
        self.assertEqual(self.request('GET', prefix + '/local-api-gate/sessions/v1.0/local-abc')[1], json.dumps(self.session).encode())
        self.assertEqual(self.request('GET', prefix + '/local-api-gate/sessions/v1.0/')[0], 200)
        self.assertEqual(self.request('POST', prefix + '/local-api-gate/sessions/v1.0/authn_token')[0], 401)
        self.assertEqual(self.request('GET', '/c/' + 'ab' * 31 + '/MobileAppConfigSdk/app/key')[0], 503)
        log = [json.loads(line) for line in self.log.read_text(encoding='utf-8').splitlines()]
        self.assertTrue(all(CODE not in json.dumps(entry) for entry in log))
        self.assertEqual(log[0]['launch_code'], True)

    def test_fixture_routes(self):
        # Byte-identical to what the Python bootstrap served (json.dumps of the fixture).
        self.assertEqual(self.request('GET', '/MobileAppConfigSdk/app/key?x=1'), (200, json.dumps({'value': self.key_value}).encode()))
        status, body = self.request('GET', '/MobileAppConfigSdk/app/policy')
        key = AES_KEY.encode()
        policy = json.loads(cbc_decrypt(key, key, base64.b64decode(json.loads(body)['value'])))
        self.assertEqual(status, 200)
        self.assertEqual(policy['custom_data']['LoginServerIP'], '127.0.0.1:' + self.env['Aion__Native__LoginPort'])
        self.assertEqual(policy['custom_data']['ChatServerURL'], 'http://127.0.0.1:' + self.env['Aion__ChatPort'])
        self.assertEqual(self.request('POST', '/MobileAppConfigSdk/app/policy/maintenance')[0], 200)
        self.assertEqual(self.request('POST', '/local-api-gate/sessions/v1.0/authn_token')[1],
                         json.dumps({'authn_token': 'local-dev-token'}).encode())
        self.assertEqual(self.request('GET', '/local-api-gate/sessions/v1.0/local-abc')[1], json.dumps(self.session).encode())
        self.assertEqual(self.request('GET', '/local-api-gate/sessions/v1.0/0b4f6a3c-1d2e-4f50-8a9b-0c1d2e3f4a5b')[0], 200)
        # A fresh saved folder has no stored session ID (the launcher's Saved_A2CorePrivate).
        self.assertEqual(self.request('GET', '/local-api-gate/sessions/v1.0/'), (200, json.dumps(self.session).encode()))
        self.assertEqual(self.request('GET', '/local-api-gate/sessions/v1.0/not-a-session')[0], 503)
        self.assertEqual(self.request('GET', '/local-api-gate/geolocations/v1.0/me'), (200, b'{"geolocation":"NO"}'))
        # Not configured (no secondary-auth fixtures) or unknown: 503, and no static files here.
        self.assertEqual(self.request('POST', '/local-api-gate/secondary_auth/v1.0/my/auth;start')[0], 503)
        self.assertEqual(self.request('GET', '/'), (503, b'{"local_probe":true,"ready":false}'))
        self.assertEqual(self.request('GET', '/MobileAppConfigSdk/app/policy/maintenance')[0], 503)
        log = [json.loads(line) for line in self.log.read_text(encoding='utf-8').splitlines()]
        self.assertEqual(log[0]['query'], '?x=1')
        self.assertIn({'path': '<other>', 'response_status': 503}, [{k: e[k] for k in ('path', 'response_status')} for e in log])


class PublicHostTests(DotnetPlatformHostTests):
    """Bound to every interface, advertising a public address (TLS proxy declared for the account port)."""
    network = {'Aion__Network__BindAddress': '0.0.0.0', 'Aion__Network__PublicAddress': '192.0.2.10',
               'Aion__Https__Proxy': 'true'}

    def test_fixture_routes(self):
        policy = self.policy()
        self.assertEqual(policy['api_gate'], f'http://192.0.2.10:{self.port}/local-api-gate')
        self.assertEqual(policy['custom_data']['LoginServerIP'], '192.0.2.10:' + self.env['Aion__Native__LoginPort'])
        self.assertEqual(policy['custom_data']['ChatServerURL'], 'http://192.0.2.10:' + self.env['Aion__ChatPort'])

    def test_launch_code_routes(self):
        self.assertEqual(self.policy('/c/' + CODE + '/MobileAppConfigSdk/app/policy')['api_gate'],
                         f'http://192.0.2.10:{self.port}/c/{CODE}/local-api-gate')


@unittest.skipUnless(HOST_DLL.exists() and shutil.which('dotnet'), 'Aion.Host is not built')
class PublicHostNeedsHttpsTests(unittest.TestCase):
    def test_refuses_plain_account_port(self):
        env = dict(os.environ, Aion__LocalPort=str(free_port()), Aion__Network__BindAddress='0.0.0.0',
                   Aion__Network__PublicAddress='192.0.2.10')
        env.pop('ConnectionStrings__Aion', None)
        env.pop('ConnectionStrings__Auth', None)
        result = subprocess.run(['dotnet', str(HOST_DLL)], cwd=HOST_DLL.parent, env=env, capture_output=True, text=True, timeout=60)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('Aion:Https', result.stdout + result.stderr)

    def test_refuses_loopback_public_address(self):
        env = dict(os.environ, Aion__LocalPort=str(free_port()), Aion__Network__BindAddress='0.0.0.0', Aion__Https__Proxy='true')
        env.pop('ConnectionStrings__Aion', None)
        env.pop('ConnectionStrings__Auth', None)
        result = subprocess.run(['dotnet', str(HOST_DLL)], cwd=HOST_DLL.parent, env=env, capture_output=True, text=True, timeout=60)
        self.assertNotEqual(result.returncode, 0)
        self.assertIn('PublicAddress', result.stdout + result.stderr)


FIXTURES = ROOT / 'research/local/bootstrap-fixtures'


@unittest.skipUnless(HOST_DLL.exists() and shutil.which('dotnet') and (FIXTURES / 'rsa-candidate-20261009').exists()
                     and (FIXTURES / 'policy-lime-20261014').exists(), 'Host or research fixtures missing')
class RealFixtureParityTests(unittest.TestCase):
    """The host must serve exactly what probe_dev_bootstrap.py served for the working chain."""

    def test_same_bytes_as_the_python_bootstrap(self):
        keys, policy = FIXTURES / 'rsa-candidate-20261009', FIXTURES / 'policy-lime-20261014'
        port = free_port()
        env = dict(os.environ, Aion__LocalPort=str(free_port()), Aion__Platform__Port=str(port),
                   Aion__Platform__FixtureDirectory=str(keys), Aion__Platform__PolicyFile=str(policy / 'app-policy.json'))
        env.pop('ConnectionStrings__Aion', None)
        env.pop('ConnectionStrings__Auth', None)
        process = subprocess.Popen(['dotnet', str(HOST_DLL)], cwd=HOST_DLL.parent, env=env,
                                   stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL)
        try:
            deadline = time.monotonic() + 30
            while time.monotonic() < deadline:
                try:
                    socket.create_connection(('127.0.0.1', port), timeout=0.5).close()
                    break
                except OSError:
                    time.sleep(0.2)

            def served(method, path):
                connection = http.client.HTTPConnection('127.0.0.1', port, timeout=5)
                connection.request(method, path, body=b'{}' if method == 'POST' else None)
                body = connection.getresponse().read()
                connection.close()
                return body

            def python(path):  # probe_dev_bootstrap: json.dumps(json.loads(fixture))
                return json.dumps(json.loads(path.read_text(encoding='utf-8'))).encode()
            def decrypted(body):
                key = json.loads((keys / 'local-test-key.json').read_text(encoding='utf-8'))['key'].encode()
                return json.loads(cbc_decrypt(key, key, base64.b64decode(json.loads(body)['value'])))
            expected = decrypted((policy / 'app-policy.json').read_bytes())
            for name in ('api_gate', 'login_web', 'login_api'):
                expected[name] = expected[name].replace('127.0.0.1:8087', f'127.0.0.1:{port}')
            self.assertEqual(decrypted(served('GET', '/MobileAppConfigSdk/app/policy')), expected)
            session = json.loads((keys / 'session-response.candidate.json').read_text(encoding='utf-8'))['session']
            for method, route, fixture in [
                    ('GET', '/MobileAppConfigSdk/app/key', keys / 'app-key.json'),
                    ('POST', '/MobileAppConfigSdk/app/policy/maintenance', keys / 'app-maintenance.json'),
                    ('POST', '/local-api-gate/sessions/v1.0/authn_token', keys / 'authn-response.candidate.json'),
                    ('POST', '/local-api-gate/secondary_auth/v1.0/my/auth;start', keys / 'secondary-start-response.candidate.json'),
                    ('POST', '/local-api-gate/secondary_auth/v1.0/my/auth;finish', keys / 'secondary-finish-response.candidate.json')]:
                self.assertEqual(served(method, route), python(fixture), route)
            # The session lookup carries the fixture's fields plus the official external_account (Steam store link).
            served_session = json.loads(served('GET', '/local-api-gate/sessions/v1.0/' + session))
            fixture_session = json.loads((keys / 'session-response.candidate.json').read_text(encoding='utf-8'))
            self.assertEqual({k: served_session[k] for k in fixture_session}, fixture_session)
            self.assertEqual(served_session['external_account']['store_auth_provider_code'], 'steam')
        finally:
            process.terminate()
            process.wait(timeout=10)


if __name__ == '__main__':
    unittest.main()


@unittest.skipUnless(HOST_DLL.exists() and shutil.which('dotnet'), 'Aion.Host is not built')
class ConfigHostTlsRoutingTests(unittest.TestCase):
    """A Steam-patched client reaches mobileappconfig.g.nc.com:443 directly (no /c/<code>). The TLS
    sink's config SNI hosts must be served by the platform bootstrap handler: app/key and a code-less,
    re-addressed app/policy, while a non-config SNI still falls to the fixture sink."""

    def setUp(self):
        import ssl
        self.ssl = ssl
        temp = tempfile.TemporaryDirectory()
        self.addCleanup(temp.cleanup)
        self.root = Path(temp.name)
        fixtures = self.root / 'fixtures'
        fixtures.mkdir()
        self.key_value = base64.b64encode(os.urandom(256)).decode()
        (fixtures / 'app-key.json').write_text(json.dumps({'value': self.key_value}))
        (fixtures / 'local-test-key.json').write_text(json.dumps({'key': AES_KEY, 'status': 'test'}))
        (fixtures / 'app-maintenance.json').write_text(json.dumps({'value': base64.b64encode(bytes(16)).decode()}))
        (fixtures / 'policy.json').write_text(json.dumps({'api_gate': 'http://127.0.0.1:8087/local-api-gate',
            'custom_data': {'LoginServerIP': '127.0.0.1:1', 'ChatServerURL': 'http://127.0.0.1:2'}}))
        (self.root / 'ncg-fixture.json').write_text(json.dumps({'rules': []}))
        self.platform_port = free_port()
        self.ncguard_port = free_port()
        self.stop_file = self.root / 'stop'
        # No native/DB services here: this exercises the config-host TLS routing only, so the policy
        # is re-addressed (api_gate) without LoginServerIP/ChatServerURL (those need the login/chat ports).
        env = dict(os.environ, Aion__LocalPort=str(free_port()), Aion__Platform__Port=str(self.platform_port),
                   Aion__Platform__FixtureDirectory=str(fixtures),
                   Aion__Platform__PolicyPlaintextFile=str(fixtures / 'policy.json'),
                   Aion__NcGuard__Port=str(self.ncguard_port), Aion__NcGuard__FixtureFile=str(self.root / 'ncg-fixture.json'),
                   Aion__NcGuard__DataDirectory=str(self.root), Aion__NcGuard__Capture='false',
                   Aion__NcGuard__DurationSeconds='120', Aion__NcGuard__StopFile=str(self.stop_file))
        env.pop('ConnectionStrings__Aion', None)
        env.pop('ConnectionStrings__Auth', None)
        self.env = env
        self.log = (self.root / 'host.log').open('w')
        self.process = subprocess.Popen(['dotnet', str(HOST_DLL)], cwd=HOST_DLL.parent, env=env,
                                        stdout=self.log, stderr=subprocess.STDOUT)
        self.addCleanup(self.stop)
        deadline = time.monotonic() + 30
        while time.monotonic() < deadline:
            if self.process.poll() is not None:
                self.fail((self.root / 'host.log').read_text())
            try:
                socket.create_connection(('127.0.0.1', self.ncguard_port), timeout=0.5).close()
                self.run = next(self.root.glob('tls-mitm-dotnet-*'))
                self.context = ssl.create_default_context(cafile=str(self.run / 'ca.pem'))
                return
            except (OSError, StopIteration):
                time.sleep(0.2)
        self.fail('host did not open the NCGuard TLS port')

    def stop(self):
        self.stop_file.touch()
        try:
            self.process.wait(timeout=10)
        except subprocess.TimeoutExpired:
            self.process.kill()
            self.process.wait()
        self.log.close()

    def tls_request(self, sni, method, path):
        with socket.create_connection(('127.0.0.1', self.ncguard_port), 5) as raw:
            with self.context.wrap_socket(raw, server_hostname=sni) as tls:
                head = (f'{method} {path} HTTP/1.1\r\nHost: {sni}\r\n'
                        f'Content-Length: 0\r\nConnection: close\r\n\r\n').encode()
                tls.sendall(head)
                response = http.client.HTTPResponse(tls)
                response.begin()
                return response.status, response.read()

    def test_config_sni_served_by_platform_without_code(self):
        # app/key: 200 with the fixture's own bytes, straight off mobileappconfig.g.nc.com:443.
        for sni in ('mobileappconfig.g.nc.com', 'mobileappconfig-ncus.g.nc.com'):
            self.assertEqual(self.tls_request(sni, 'GET', '/MobileAppConfigSdk/app/key'),
                             (200, json.dumps({'value': self.key_value}).encode()), sni)
        # app/policy: 200, and a code-less policy re-addressed to this host's bootstrap port.
        status, body = self.tls_request('mobileappconfig.g.nc.com', 'GET', '/MobileAppConfigSdk/app/policy')
        self.assertEqual(status, 200)
        key = AES_KEY.encode()
        policy = json.loads(cbc_decrypt(key, key, base64.b64decode(json.loads(body)['value'])))
        # With NcGuard on, the code-less policy re-addresses onto the HTTPS config host (api.g.nc.com).
        self.assertEqual(policy['api_gate'], 'https://api.g.nc.com/local-api-gate')
        self.assertNotIn('/c/', policy['api_gate'])

    def test_non_config_sni_still_hits_the_sink(self):
        # bladnoch is an NCGuard host, not a config host: its /MobileAppConfigSdk path is not a
        # platform route, so the fixture sink answers (404), proving only config SNIs are diverted.
        status, body = self.tls_request('bladnoch.ncsoft.com', 'GET', '/MobileAppConfigSdk/app/key')
        self.assertEqual(status, 404)
        self.assertNotIn(b'value', body)
