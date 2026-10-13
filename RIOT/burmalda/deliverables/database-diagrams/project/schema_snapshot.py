"""Read-only PostgreSQL schema inventory for the combined Burmalda diagram."""
import json
import subprocess
from datetime import datetime, timezone
from pathlib import Path

DATABASES = ['identity_db', 'catalog_db', 'lending_db', 'economy_db',
             'reputation_db', 'notification_db', 'concurrency_lab_db']
ROOT = Path(__file__).resolve().parent

def query(database, sql):
    result = subprocess.run(['docker', 'exec', 'burmalda-postgres', 'psql', '-X', '-A', '-t',
                             '-U', 'burmalda_admin', '-d', database, '-c', sql],
                            check=True, capture_output=True, text=True, encoding='utf-8')
    return json.loads(result.stdout)

def snapshot():
    data = {'checked_at_utc': datetime.now(timezone.utc).isoformat(), 'databases': {}}
    for database in DATABASES:
        columns = query(database, """
          SELECT coalesce(json_agg(x ORDER BY table_schema,table_name,ordinal_position),'[]')
          FROM (SELECT table_schema,table_name,column_name,ordinal_position,udt_name,
                       is_nullable,character_maximum_length,numeric_precision,numeric_scale,
                       column_default,is_identity,identity_generation
                FROM information_schema.columns
                WHERE table_schema != 'information_schema' AND table_schema !~ '^pg_') x
        """)
        indexes = query(database, """
          SELECT coalesce(json_agg(x ORDER BY table_schema,table_name,index_name),'[]') FROM (
            SELECT ns.nspname AS table_schema,t.relname AS table_name,i.relname AS index_name,
                   ix.indisprimary AS is_primary,ix.indisunique AS is_unique,
                   ARRAY(SELECT a.attname FROM unnest(ix.indkey) WITH ORDINALITY k(attnum,n)
                         JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum=k.attnum
                         WHERE k.n <= ix.indnkeyatts ORDER BY k.n) AS columns,
                   ARRAY(SELECT a.attname FROM unnest(ix.indkey) WITH ORDINALITY k(attnum,n)
                         JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum=k.attnum
                         WHERE k.n > ix.indnkeyatts ORDER BY k.n) AS include_columns,
                   pg_get_expr(ix.indpred,ix.indrelid) AS predicate,
                   pg_get_indexdef(i.oid) AS definition
            FROM pg_index ix JOIN pg_class t ON t.oid=ix.indrelid
            JOIN pg_class i ON i.oid=ix.indexrelid
            JOIN pg_namespace ns ON ns.oid=t.relnamespace
            WHERE ns.nspname != 'information_schema' AND ns.nspname !~ '^pg_'
          ) x
        """)
        constraints = query(database, """
          SELECT coalesce(json_agg(x ORDER BY table_schema,table_name,name),'[]') FROM (
            SELECT n.nspname AS table_schema,t.relname AS table_name,c.conname AS name,
                   c.contype AS kind,pg_get_constraintdef(c.oid) AS definition,
                   ARRAY(SELECT a.attname FROM unnest(c.conkey) WITH ORDINALITY k(attnum,i)
                         JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum=k.attnum
                         ORDER BY k.i) AS columns,
                   rn.nspname AS target_schema,rt.relname AS target_table,
                   ARRAY(SELECT a.attname FROM unnest(c.confkey) WITH ORDINALITY k(attnum,i)
                         JOIN pg_attribute a ON a.attrelid=rt.oid AND a.attnum=k.attnum
                         ORDER BY k.i) AS target_columns
            FROM pg_constraint c JOIN pg_class t ON t.oid=c.conrelid
            JOIN pg_namespace n ON n.oid=t.relnamespace
            LEFT JOIN pg_class rt ON rt.oid=c.confrelid
            LEFT JOIN pg_namespace rn ON rn.oid=rt.relnamespace
            WHERE n.nspname != 'information_schema' AND n.nspname !~ '^pg_'
          ) x
        """)
        data['databases'][database] = {'columns': columns, 'indexes': indexes,
                                       'constraints': constraints}
        tables = {(c['table_schema'], c['table_name']) for c in columns}
        print(f'{database}: {len(tables)} tables, {len(columns)} columns, '
              f'{sum(c["kind"] == "f" for c in constraints)} physical FKs')
    return data

if __name__ == '__main__':
    ROOT.mkdir(parents=True, exist_ok=True)
    (ROOT / 'schema-snapshot.json').write_text(json.dumps(snapshot(), indent=2) + '\n', encoding='utf-8')
