"""Local PostgreSQL lifecycle, checked migrations, catalogue import and backups.

Uses vendor psql/pg_ctl tools, not a per-request game-server database driver.
Credentials and all generated data stay under ignored research/local/postgres.

Linux servers (deploy/linux/README.md): the same project-owned cluster, with the
distribution's PostgreSQL binaries (AION_PG_BIN, else the newest
/usr/lib/postgresql/<n>/bin) and AION_PG_HOME for the cluster and credentials.

Never edit a migration that may already be applied on a server. Add a new numbered
file instead. If an in-place fix is unavoidable, register the previous file hash in
AMENDED_MIGRATION_LEGACY_SHA256 (see database/migrations/README.md).
"""
import argparse
import csv
import glob
import hashlib
import json
import locale
import os
from pathlib import Path
import secrets
import sys
import subprocess
import tempfile
import zipfile

ROOT = Path(__file__).resolve().parents[1]
HOME = Path(os.environ['AION_PG_HOME']) if os.environ.get('AION_PG_HOME') else ROOT / 'research/local/postgres'
PORT = 55432
WINDOWS = os.name == 'nt'


def binaries():
    """Windows: the project's extracted binaries. Elsewhere: the distribution's server binaries."""
    if os.environ.get('AION_PG_BIN'):
        return Path(os.environ['AION_PG_BIN'])
    if WINDOWS:
        return HOME / 'pgsql/bin'
    versions = sorted(glob.glob('/usr/lib/postgresql/*/bin'), key=lambda p: int(Path(p).parent.name) if Path(p).parent.name.isdigit() else 0)
    return Path(versions[-1]) if versions else Path('/usr/bin')


BIN = binaries()

# Release filenames -> prior committed sha256 (LF-normalized) values still on live DBs.
# On match, schema_migrations.sha256 is updated to the current file hash without re-running SQL.
AMENDED_MIGRATION_LEGACY_SHA256: dict[str, frozenset[str]] = {
  # #93339c51: append-only trigger wrap on quna_kind backfill only; successful old applies already backfilled.
    '141_shop_feature_layer.sql': frozenset({
        'f270875eb4344bd8ac0c7516b22e8588a7ca47400e2ca4bf910e31498f13e068',
    }),
  # f2b62c79 briefly inlined slot_index into 134; d63bed65 moved it to 135 and restored this file body.
    '134_qa_bots_dedup.sql': frozenset({
        'c1e51d1a0861bd2b8f8ec76f82007b2322408a07e4125167991d24858e189c8d',
    }),
}


def literal(text):
    return "'" + str(text).replace("'", "''") + "'"


def tool_encoding(binary):
    """psql is asked for UTF-8. initdb and pg_ctl print in the Windows ANSI code page."""
    if not WINDOWS or binary in {'psql', 'pg_dump', 'pg_restore'}:
        return 'utf-8'
    return locale.getpreferredencoding(False)


def run(binary, args=(), *, text=None, role='aion_admin', database='aion', check=True, timeout=180):
    env = os.environ.copy()
    if (HOME / 'credentials.json').exists():
        credentials = json.loads((HOME / 'credentials.json').read_text())
        env.update(PGHOST='127.0.0.1', PGPORT=str(PORT), PGUSER=role,
                   PGPASSWORD=credentials[role], PGDATABASE=database, PGCONNECT_TIMEOUT='5')
    # Query results stay UTF-8. Connection errors on Windows may still be ANSI; those are decoded with replacement.
    env['PGCLIENTENCODING'] = 'UTF8'
    command = [str(BIN / (binary + ('.exe' if WINDOWS else ''))), *map(str, args)]
    flags = getattr(subprocess, 'CREATE_NO_WINDOW', 0)
    if binary == 'pg_ctl':
        # A detached Windows postgres may inherit pipes and keep communicate()
        # waiting after pg_ctl exits. Use a file and DEVNULL for lifecycle calls.
        # The child writes its console code page straight to the handle, so read the bytes back.
        with (HOME / 'lifecycle.log').open('w+b') as log:
            process = subprocess.run(command, stdin=subprocess.DEVNULL, stdout=log,
                                     stderr=subprocess.STDOUT, env=env,
                                     creationflags=flags, timeout=45)
            log.seek(0)
            message = log.read().decode(tool_encoding(binary), errors='replace')
            result = subprocess.CompletedProcess(command, process.returncode, '', message)
    else:
        result = subprocess.run(command, input=text, encoding=tool_encoding(binary), errors='replace',
                                capture_output=True, env=env, creationflags=flags, timeout=timeout)
    if check and result.returncode:
        # SQL errors can contain input; redact generated credentials before printing.
        message = result.stderr
        if (HOME / 'credentials.json').exists():
            for password in json.loads((HOME / 'credentials.json').read_text()).values():
                message = message.replace(password, '[redacted]')
        raise RuntimeError(f'{binary} failed: {message}')
    return result


def sql(text, *, role='aion_admin', database='aion', check=True):
    return run('psql', ['-X', '-q', '-A', '-t', '-v', 'ON_ERROR_STOP=1'], text=text,
               role=role, database=database, check=check)


def setup():
    HOME.mkdir(parents=True, exist_ok=True)
    archive = HOME / 'postgresql-18.6-1-windows-x64-binaries.zip'
    if not (BIN / ('initdb' + ('.exe' if WINDOWS else ''))).exists() and not WINDOWS:
        raise ValueError(f'No PostgreSQL server binaries in {BIN}; install postgresql or set AION_PG_BIN')
    if not BIN.exists():
        with zipfile.ZipFile(archive) as bundle:
            for member in bundle.infolist():
                relative = Path(member.filename)
                if len(relative.parts) < 2 or relative.parts[:2] not in (
                        ('pgsql', 'bin'), ('pgsql', 'lib'), ('pgsql', 'share')):
                    continue
                destination = (HOME / relative).resolve()
                if not destination.is_relative_to(HOME.resolve()):
                    raise ValueError('Unsafe archive path')
                bundle.extract(member, HOME)
        with archive.open('rb') as downloaded:
            archive_digest = hashlib.file_digest(downloaded, 'sha256').hexdigest()
        (HOME / 'binary-source.json').write_text(json.dumps({
            'url': 'https://get.enterprisedb.com/postgresql/' + archive.name,
            'sha256': archive_digest
        }, indent=2), encoding='utf-8')
    credentials_path = HOME / 'credentials.json'
    if not credentials_path.exists():
        if (HOME / 'data').exists():
            raise ValueError('Existing cluster has no credentials file; refusing to replace credentials')
        credentials_path.write_text(json.dumps({name: secrets.token_urlsafe(36)
            for name in ('aion_admin', 'aion_app', 'aion_auth')}), encoding='utf-8')
    credentials = json.loads(credentials_path.read_text())
    if 'aion_auth' not in credentials:
        credentials['aion_auth'] = secrets.token_urlsafe(36)
        credentials_path.write_text(json.dumps(credentials), encoding='utf-8')
    if not (HOME / 'data/PG_VERSION').exists():
        password_file = HOME / 'init-password.txt'
        password_file.write_text(credentials['aion_admin'], encoding='ascii')
        try:
            run('initdb', ['-D', HOME / 'data', '-U', 'aion_admin', '--encoding=UTF8',
                          '--locale=C', '--auth=scram-sha-256', '--pwfile', password_file])
        finally:
            password_file.unlink(missing_ok=True)
        with (HOME / 'data/postgresql.conf').open('a', encoding='utf-8') as config:
            # TCP on loopback only. On Linux, drop the Unix socket so this cluster does not
            # take the distribution's socket directory. Windows builds reject that parameter.
            config.write("\n# Project-owned cluster: loopback only\nlisten_addresses='127.0.0.1'\n"
                         f"port={PORT}\npassword_encryption='scram-sha-256'\n")
            if not WINDOWS:
                config.write("unix_socket_directories=''\n")
    start()
    sql("SELECT 'CREATE ROLE aion_owner NOLOGIN' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname='aion_owner')\\gexec\n"
        "SELECT 'CREATE ROLE aion_app LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname='aion_app')\\gexec\n"
        "SELECT 'CREATE ROLE aion_auth LOGIN NOSUPERUSER NOCREATEDB NOCREATEROLE' WHERE NOT EXISTS (SELECT FROM pg_roles WHERE rolname='aion_auth')\\gexec\n"
        'ALTER ROLE aion_app PASSWORD ' + literal(credentials['aion_app']) + ';\n'
        'ALTER ROLE aion_auth PASSWORD ' + literal(credentials['aion_auth']) + ';\n'
        "SELECT 'CREATE DATABASE aion OWNER aion_owner' WHERE NOT EXISTS (SELECT FROM pg_database WHERE datname='aion')\\gexec\n",
        database='postgres')
    sql('REVOKE ALL ON DATABASE aion FROM PUBLIC; GRANT CONNECT ON DATABASE aion TO aion_app,aion_auth;')
    migrate()


def start():
    # Windows sandbox process inspection can report a running server as stopped.
    # Prefer an authenticated check and verify this is our own cluster first.
    probe = sql("SELECT current_setting('data_directory');", database='postgres', check=False)
    if probe.returncode == 0:
        if os.path.normcase(str(Path(probe.stdout.strip()).resolve())) != os.path.normcase(str((HOME / 'data').resolve())):
            raise ValueError('Port is serving a different PostgreSQL data directory')
        return
    status = run('pg_ctl', ['status', '-D', HOME / 'data'], check=False)
    if status.returncode:
        run('pg_ctl', ['start', '-D', HOME / 'data', '-l', HOME / 'server.log', '-w', '-t', '30'])


def migration_check_sql(version: str, digest: str, legacies: frozenset[str]) -> str:
    """Hash guard for one migration file; legacies allow a one-time sha256 bump without re-applying."""
    if not legacies:
        return (f"DO $check$ BEGIN IF EXISTS(SELECT FROM public.schema_migrations WHERE version={version} "
                f"AND sha256<>{digest}) THEN RAISE EXCEPTION 'Applied migration changed'; END IF; END $check$;")
    legacy_sql = ','.join(literal(h) for h in sorted(legacies))
    return (f"DO $check$ BEGIN IF EXISTS(SELECT FROM public.schema_migrations WHERE version={version} "
            f"AND sha256<>{digest}) THEN IF (SELECT sha256 FROM public.schema_migrations WHERE version={version}) "
            f"IN ({legacy_sql}) THEN UPDATE public.schema_migrations SET sha256={digest} WHERE version={version}; "
            f"ELSE RAISE EXCEPTION 'Applied migration changed'; END IF; END IF; END $check$;")


def migration_script(files):
    out = ["BEGIN; SELECT pg_advisory_xact_lock(731924001); SET LOCAL ROLE aion_owner;",
           'CREATE TABLE IF NOT EXISTS public.schema_migrations(version text PRIMARY KEY, sha256 text NOT NULL, applied_at timestamptz NOT NULL DEFAULT now());']
    for path in files:
        # Hash the committed LF form. A Windows checkout may rewrite the same file as CRLF.
        raw = path.read_bytes().replace(b'\r\n', b'\n')
        body = raw.decode('utf-8')
        version, digest = literal(path.name), literal(hashlib.sha256(raw).hexdigest())
        legacies = AMENDED_MIGRATION_LEGACY_SHA256.get(path.name, frozenset())
        out.extend([migration_check_sql(version, digest, legacies),
                    f'SELECT NOT EXISTS(SELECT FROM public.schema_migrations WHERE version={version}) AS apply \\gset',
                    '\\if :apply', body,
                    f'INSERT INTO public.schema_migrations(version,sha256) VALUES({version},{digest});', '\\endif'])
    return '\n'.join(out + ['COMMIT;'])


def migrate():
    sql(migration_script(sorted((ROOT / 'database/migrations').glob('*.sql'))))


def import_catalog(directory, allow_shrink=False):
    directory = Path(directory)
    manifest = json.loads((directory / 'manifest.json').read_text(encoding='utf-8'))
    canonical = json.dumps(manifest, sort_keys=True, separators=(',', ':'))
    build_id = hashlib.sha256(canonical.encode()).hexdigest()
    with tempfile.NamedTemporaryFile(mode='w', encoding='utf-8', newline='', suffix='.sql',
                                     dir=HOME, delete=False) as stream:
        path = Path(stream.name)
        stream.write('BEGIN; SELECT pg_advisory_xact_lock(731924002); SET LOCAL ROLE aion_owner;\n')
        stream.write('INSERT INTO catalog.builds(build_id,manifest) VALUES(' + literal(build_id) + ',' + literal(canonical) + ') ON CONFLICT DO NOTHING;\n')
        stream.write('CREATE TEMP TABLE incoming(document jsonb);\nCOPY incoming FROM STDIN WITH (FORMAT csv);\n')
        writer = csv.writer(stream, lineterminator='\n')
        counts = {'items': 0, 'skills': 0}
        with (directory / 'entries.jsonl').open(encoding='utf-8') as source:
            for line in source:
                document = json.loads(line)
                counts[document['kind']] += 1
                writer.writerow([json.dumps(document, ensure_ascii=False)])
        if counts != manifest['counts']:
            raise ValueError('Export counts differ from manifest')
        previous_items = {int(x) for x in sql(
            "SELECT COALESCE(string_agg(template_id::text, ','), '') FROM catalog.items "
            "WHERE build_id=(SELECT build_id FROM catalog.builds ORDER BY imported_at DESC, build_id LIMIT 1);"
        ).stdout.strip().split(',') if x}
        previous_skills = {int(x) for x in sql(
            "SELECT COALESCE(string_agg(template_id::text, ','), '') FROM catalog.skills "
            "WHERE build_id=(SELECT build_id FROM catalog.builds ORDER BY imported_at DESC, build_id LIMIT 1);"
        ).stdout.strip().split(',') if x}
        incoming_ids = {'items': set(), 'skills': set()}
        with (directory / 'entries.jsonl').open(encoding='utf-8') as source:
            for line in source:
                document = json.loads(line)
                incoming_ids[document['kind']].add(int(document['id']))
        shrink_items = sorted(previous_items - incoming_ids['items'])
        shrink_skills = sorted(previous_skills - incoming_ids['skills'])
        if (shrink_items or shrink_skills) and not allow_shrink:
            raise ValueError(f'Catalogue snapshot shrinks the previous build (items={len(shrink_items)} skills={len(shrink_skills)}); pass allow_shrink=True to override')
        print(f"catalogue import: items {len(previous_items)} -> {counts['items']} "
              f"(+{len(incoming_ids['items']-previous_items)} -{len(shrink_items)}); "
              f"skills {len(previous_skills)} -> {counts['skills']}")
        stream.write('\\.\n')
        for kind in ('items', 'skills'):
            # Immutable snapshots: the same build/ID may be reimported only unchanged.
            stream.write(f"DO $verify$ BEGIN IF EXISTS(SELECT FROM incoming i JOIN catalog.{kind} c ON c.build_id={literal(build_id)} AND c.template_id=(i.document->>'id')::integer WHERE i.document->>'kind'='{kind}' AND c.definition<>i.document) THEN RAISE EXCEPTION 'Catalogue snapshot changed'; END IF; END $verify$;\n")
            stream.write(f"INSERT INTO catalog.{kind}(build_id,template_id,name,category,definition) SELECT {literal(build_id)},(document->>'id')::integer,document->>'name',document->>'category',document FROM incoming WHERE document->>'kind'='{kind}' ON CONFLICT DO NOTHING;\n")
        stream.write('COMMIT;\n')
    try:
        run('psql', ['-X', '-q', '-v', 'ON_ERROR_STOP=1', '-f', path], timeout=1800)
    finally:
        path.unlink(missing_ok=True)
    result = {'build_id': build_id, 'counts': counts}
    latest = sql('SELECT build_id FROM catalog.builds ORDER BY imported_at DESC, build_id LIMIT 1;').stdout.strip()
    if latest == build_id:
        for script, output in run_starter_tools(build_id):
            if output:
                print(output)
    return result


def latest_build_id():
    """The catalogue build new item grants use (ORDER BY imported_at DESC)."""
    return sql('SELECT build_id FROM catalog.builds ORDER BY imported_at DESC, build_id LIMIT 1;').stdout.strip() or None


def starter_data_complete(build_id=None):
    """True when game.equipment_rules and enabled starter_profiles exist for the build."""
    build_id = build_id or latest_build_id()
    if not build_id:
        return False
    rules = int(sql(f"SELECT count(*) FROM game.equipment_rules WHERE build_id={literal(build_id)};").stdout.strip() or '0')
    profiles = int(sql('SELECT count(*) FROM game.starter_profiles WHERE enabled;').stdout.strip() or '0')
    return rules > 0 and profiles > 0


DEFAULT_CATALOG = ROOT / 'research/local/game-catalog-v2'
STARTER_SETUP_TOOLS = (
    ('configure_starters.py',),
    ('class_skills.py', 'install'),
    ('equipment_rules.py',),
)
MISSING_CATALOG_MESSAGE = """The item/skill catalogue is not imported and the catalogue directory is missing, so character creation will fail with profile_unavailable.

To build research/local/game-catalog-v2:
  1. Extract client tables with tools/WorldProbe into a new folder under research/local/
     (command and selection limits are in the header of tools/WorldProbe/Program.cs).
  2. Run: python tools/game_catalog.py build
     It reads research/local/reuse-table-probe (Item.dat, Skill.dat) and related tables
     and writes research/local/game-catalog-v2 (manifest.json and entries.jsonl).

Or unpack aion-server-data.tar.gz from the Releases page (it includes game-catalog-v2/)
into research/local/ on Windows, or pass it to deploy/linux/install.sh --data on Linux.

Then re-run start-all.cmd / install.sh, or:
  python tools/postgres_dev.py import-catalog
  python tools/configure_starters.py
  python tools/class_skills.py install
  python tools/equipment_rules.py
"""
NO_STARTERS_MESSAGE = """No enabled rows in game.starter_profiles; character creation will fail with profile_unavailable.
The catalogue may be missing starter weapons or skills for the classes. Re-run:
  python tools/configure_starters.py
  python tools/class_skills.py install
  python tools/equipment_rules.py
"""


def catalog_files_present(directory):
    directory = Path(directory)
    return (directory / 'manifest.json').is_file() and (directory / 'entries.jsonl').is_file()


def run_starter_tools(build_id=None, *, runner=None):
    """Run configure_starters, class_skills install and equipment_rules. All three are safe to repeat."""
    runner = runner or subprocess.run
    extra = ('--build-id', build_id) if build_id else ()
    for script, *args in STARTER_SETUP_TOOLS:
        command = [sys.executable, str(ROOT / 'tools' / script), *args, *extra]
        result = runner(command, cwd=ROOT, capture_output=True, text=True, check=False)
        if result.returncode:
            detail = (result.stderr or result.stdout or '').strip() or f'exit {result.returncode}'
            raise RuntimeError(f'{script} failed: {detail}')
        yield script, (result.stdout or '').strip()


def ensure_game_data(directory=None, *, log=print, runner=None, force=False):
    """Import the catalogue when catalog.builds is empty, then install starter policy if needed.

    configure_starters, class_skills install and equipment_rules are idempotent (ON CONFLICT / rebuild).
    They run after a fresh import, when no enabled starter profiles exist, or when force=True
    (Windows start-all re-applies them every time). Missing catalogue files are a warning, not
    an error: the database can still start.
    """
    directory = Path(directory or DEFAULT_CATALOG)
    builds = int(sql('SELECT count(*) FROM catalog.builds;').stdout.strip() or '0')
    imported = False
    if builds == 0:
        if catalog_files_present(directory):
            log(f'Importing item/skill catalogue from {directory}')
            import_catalog(directory)
            imported = True
        else:
            log(MISSING_CATALOG_MESSAGE)
            return {'imported': False, 'configured': False, 'enabled': 0, 'build_id': None}
    enabled = int(sql('SELECT count(*) FROM game.starter_profiles WHERE enabled;').stdout.strip() or '0')
    build_id = latest_build_id()
    configured = False
    needs_starters = imported or force or enabled == 0 or (build_id and not starter_data_complete(build_id))
    if needs_starters:
        log('Configuring starter profiles, class skills and equipment rules')
        for script, output in run_starter_tools(build_id, runner=runner):
            if output:
                log(output)
        configured = True
        enabled = int(sql('SELECT count(*) FROM game.starter_profiles WHERE enabled;').stdout.strip() or '0')
    if enabled == 0:
        log(NO_STARTERS_MESSAGE)
    else:
        log(f'Starter profiles ready: {enabled} enabled.')
    return {'imported': imported, 'configured': configured, 'enabled': enabled, 'build_id': build_id}


def verify_backup(path):
    """Restore into a uniquely named disposable DB; never restore over aion."""
    name = 'aion_restore_' + secrets.token_hex(8)
    sql(f'CREATE DATABASE {name};', database='postgres')
    try:
        run('pg_restore', ['--no-owner', '--no-privileges', '--exit-on-error',
                           '--dbname', name, Path(path).resolve()])
        return sql("SELECT 'items='||count(*) FROM catalog.items;"
                   "SELECT 'skills='||count(*) FROM catalog.skills;"
                   "SELECT 'migrations='||count(*) FROM public.schema_migrations;", database=name).stdout.strip()
    finally:
        # Name consists only of this fixed prefix and freshly generated hex.
        sql(f'DROP DATABASE {name};', database='postgres')


ROLES = ('gamemaster', 'admin')


def role_statement(username, role, revoke=False):
    """Operator-only staff role change for a local account; returns the SQL to run."""
    if role not in ROLES or not username or len(username) > 32 or not all(c.isalnum() or c == '_' for c in username):
        raise ValueError('Use a local username and a role of gamemaster or admin')
    account = f"SELECT account_id FROM auth.accounts WHERE provider='local' AND subject={literal(username)}"
    if revoke:
        return f"DELETE FROM auth.account_roles WHERE role={literal(role)} AND account_id=({account}) RETURNING account_id;"
    # Staff standing in the world is per character (migration 035): a grant also flags the account's existing characters.
    return (f"WITH granted AS (INSERT INTO auth.account_roles SELECT account_id,{literal(role)} FROM auth.accounts "
            f"WHERE provider='local' AND subject={literal(username)} ON CONFLICT DO NOTHING RETURNING account_id), "
            f"flagged AS (UPDATE game.characters SET staff=true WHERE NOT staff AND account_id=({account})) "
            "SELECT account_id FROM granted;")


def main():
    parser = argparse.ArgumentParser(description=__doc__)
    sub = parser.add_subparsers(dest='command', required=True)
    for command in ('setup', 'start', 'stop', 'status', 'migrate'):
        sub.add_parser(command)
    load = sub.add_parser('import-catalog')
    load.add_argument('--directory', type=Path, default=DEFAULT_CATALOG)
    load.add_argument('--allow-shrink', action='store_true')
    ensure = sub.add_parser('ensure-game-data', help='import the catalogue if needed and install starter profiles')
    ensure.add_argument('--directory', type=Path, default=DEFAULT_CATALOG)
    ensure.add_argument('--force', action='store_true', help='re-apply starter tools even when enabled profiles already exist')
    backup = sub.add_parser('backup')
    backup.add_argument('output', type=Path)
    verify = sub.add_parser('verify-backup')
    verify.add_argument('path', type=Path)
    staff = sub.add_parser('role', help='grant or revoke a gamemaster/admin role (operator only)')
    staff.add_argument('username')
    staff.add_argument('role', choices=ROLES)
    staff.add_argument('--revoke', action='store_true')
    args = parser.parse_args()
    if args.command == 'setup':
        setup()
        print('PostgreSQL configured at 127.0.0.1:55432, database aion; migrations applied.')
    elif args.command == 'start':
        start()
    elif args.command == 'stop':
        run('pg_ctl', ['stop', '-D', HOME / 'data', '-m', 'fast', '-w'])
    elif args.command == 'status':
        print(sql("SELECT version(); SELECT 'items='||count(*) FROM catalog.items; SELECT 'skills='||count(*) FROM catalog.skills;").stdout)
    elif args.command == 'migrate':
        migrate()
    elif args.command == 'import-catalog':
        print(json.dumps(import_catalog(args.directory, allow_shrink=args.allow_shrink), indent=2))
    elif args.command == 'ensure-game-data':
        print(json.dumps(ensure_game_data(args.directory, force=args.force), indent=2))
    elif args.command == 'backup':
        if args.output.exists():
            raise ValueError('Backup destination already exists')
        args.output.parent.mkdir(parents=True, exist_ok=True)
        run('pg_dump', ['-Fc', '-f', args.output])
        print('Backup saved:', args.output)
    elif args.command == 'verify-backup':
        print(verify_backup(args.path))
    elif args.command == 'role':
        changed = sql(role_statement(args.username, args.role, args.revoke)).stdout.strip()
        exists = sql(f"SELECT count(*) FROM auth.accounts WHERE provider='local' AND subject={literal(args.username)};").stdout.strip()
        if exists == '0':
            raise SystemExit(f'No local account named {args.username}.')
        action = 'Revoked' if args.revoke else 'Granted'
        print(f'{action} {args.role} for {args.username}.' if changed else f'No change: {args.username} already {"lacks" if args.revoke else "has"} {args.role}.')


if __name__ == '__main__':
    main()
