# SQL migrations

Applied once per server, recorded in `public.schema_migrations` with the filename and a SHA-256 of the file bytes (LF line endings). `tools/postgres_dev.py migrate` is used by local dev, Linux `deploy/linux/update.sh`, and the host (`PostgresMigrationRunner`).

**Do not edit a migration after it has shipped.** Servers that already applied the old text will refuse to update (`Applied migration changed`) unless the previous hash is registered in `AMENDED_MIGRATION_LEGACY_SHA256` in `tools/postgres_dev.py`—and that only bumps the stored hash; it does not re-run SQL. If the database still needs a schema or data change, add a **new** numbered `NNN_description.sql` instead.

Renaming a migration file (e.g. renumbering) leaves the old version string in `schema_migrations`; the new filename is treated as a separate migration. Prefer leaving the filename stable or add a follow-up migration rather than rename in place.