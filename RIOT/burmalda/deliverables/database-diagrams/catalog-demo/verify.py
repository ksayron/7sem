"""Compare the editable diagram with live PostgreSQL metadata (read-only)."""
import json
import subprocess
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from pathlib import Path

root = Path(__file__).resolve().parent


def query(sql):
    result = subprocess.run(
        ["docker", "exec", "burmalda-postgres", "psql", "-X", "-A", "-t",
         "-U", "burmalda_admin", "-d", "catalog_db", "-c", sql],
        check=True, capture_output=True, text=True, encoding="utf-8")
    return json.loads(result.stdout)


columns = query("""
SELECT json_agg(x ORDER BY table_name, ordinal_position) FROM (
 SELECT table_name, column_name, ordinal_position, udt_name, is_nullable,
        character_maximum_length, numeric_precision, numeric_scale
 FROM information_schema.columns WHERE table_schema = 'public'
) x
""")
indexes = query("""
SELECT json_agg(x ORDER BY table_name, index_name) FROM (
 SELECT t.relname AS table_name, i.relname AS index_name,
        ix.indisprimary AS is_primary, ix.indisunique AS is_unique,
        ARRAY(SELECT a.attname FROM unnest(ix.indkey) WITH ORDINALITY k(attnum,n)
              JOIN pg_attribute a ON a.attrelid=t.oid AND a.attnum=k.attnum
              ORDER BY k.n) AS columns
 FROM pg_index ix JOIN pg_class t ON t.oid=ix.indrelid
 JOIN pg_class i ON i.oid=ix.indexrelid
 JOIN pg_namespace ns ON ns.oid=t.relnamespace WHERE ns.nspname='public'
) x
""")
physical_fks = query("""
SELECT count(*) FROM pg_constraint c JOIN pg_namespace n ON n.oid=c.connamespace
WHERE n.nspname='public' AND c.contype='f'
""")

model = ET.parse(root / "burmalda-catalog-demo.dbs").getroot()
tables = {t.attrib["name"]: t for t in model.findall("schema/table")}
assert set(tables) == {c["table_name"] for c in columns}
type_names = {"int4": "integer", "int8": "bigint", "numeric": "decimal",
              "bool": "boolean", "varchar": "varchar"}
assert sum(len(t.findall("column")) for t in tables.values()) == len(columns)
for c in columns:
    column = next(x for x in tables[c["table_name"]].findall("column")
                  if x.attrib["name"] == c["column_name"])
    assert column.attrib["type"] == type_names.get(c["udt_name"], c["udt_name"]), c
    assert (column.get("mandatory") == "y") == (c["is_nullable"] == "NO"), c
    if c["character_maximum_length"]:
        assert int(column.attrib["length"]) == c["character_maximum_length"], c
    if c["udt_name"] == "numeric" and c["numeric_precision"]:
        assert int(column.attrib["length"]) == c["numeric_precision"], c
        assert int(column.attrib["decimal"]) == c["numeric_scale"], c
assert sum(len(t.findall("index")) for t in tables.values()) == len(indexes)
for i in indexes:
    index = next(x for x in tables[i["table_name"]].findall("index")
                 if x.attrib["name"] == i["index_name"])
    assert [x.attrib["name"] for x in index.findall("column")] == i["columns"], i
    assert (index.get("unique") == "PRIMARY_KEY") == i["is_primary"], i
    assert (index.get("unique") in ("PRIMARY_KEY", "UNIQUE_KEY", "UNIQUE_INDEX")) == i["is_unique"], i
assert physical_fks == 0
relations = model.findall("schema/table/fk")
assert len(relations) == 2 and all(r.get("virtual") == "y" for r in relations)
assert not model.findall("connector"), "Diagram must not contain saved connections"
report = {
    "checked_at_utc": datetime.now(timezone.utc).isoformat(), "database": "catalog_db",
    "status": "passed", "tables": len(tables), "columns": len(columns),
    "indexes": len(indexes), "physical_foreign_keys": physical_fks,
    "virtual_relationships": len(relations), "columns_snapshot": columns,
    "indexes_snapshot": indexes,
    "checks": ["table coverage", "column coverage and types", "nullability",
               "varchar lengths and numeric precision", "index columns and uniqueness",
               "virtual links only", "no saved database connections"]}
(root / "verification.json").write_text(json.dumps(report, indent=2) + "\n", encoding="utf-8")
print(f"Verified {len(tables)} tables, {len(columns)} columns, {len(indexes)} indexes; PostgreSQL unchanged.")
