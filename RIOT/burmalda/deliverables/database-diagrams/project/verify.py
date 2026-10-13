"""Verify all tables, relationships, and service-group membership against PostgreSQL."""
import json
import xml.etree.ElementTree as ET
from datetime import datetime, timezone
from schema_snapshot import ROOT, snapshot

before = json.loads((ROOT / 'schema-snapshot.json').read_text(encoding='utf-8'))
live = snapshot()
assert before['databases'] == live['databases'], 'PostgreSQL metadata changed during generation'
model = ET.parse(ROOT / 'burmalda-all-databases.dbs').getroot()
tables = {(s.get('catalogname'), s.get('name'), t.get('name')): t
          for s in model.findall('schema') for t in s.findall('table')}
type_names = {'int4': 'integer', 'int8': 'bigint', 'numeric': 'decimal',
              'bool': 'boolean', 'float8': 'double precision'}
expected_tables = set()
index_count = 0
for database, metadata in live['databases'].items():
    for c in metadata['columns']:
        key = (database, c['table_schema'], c['table_name'])
        expected_tables.add(key)
        table = tables[key]
        column = next(x for x in table.findall('column') if x.get('name') == c['column_name'])
        assert column.get('type') == type_names.get(c['udt_name'], c['udt_name']), c
        assert (column.get('mandatory') == 'y') == (c['is_nullable'] == 'NO'), c
        if c['character_maximum_length']:
            assert int(column.get('length')) == c['character_maximum_length'], c
        if c['udt_name'] == 'numeric' and c['numeric_precision']:
            assert int(column.get('length')) == c['numeric_precision'], c
            assert int(column.get('decimal')) == c['numeric_scale'], c
        if c['is_identity'] == 'YES':
            identity = column.findtext('identity') or ''
            assert c['identity_generation'] in ' '.join(identity.split()), c
    for i in metadata['indexes']:
        index_count += 1
        table = tables[(database, i['table_schema'], i['table_name'])]
        index = next(x for x in table.findall('index') if x.get('name') == i['index_name'])
        assert [x.get('name') for x in index.findall('column')] == i['columns'], i
        assert (index.get('unique') == 'PRIMARY_KEY') == i['is_primary'], i
        assert (index.get('unique') in ('PRIMARY_KEY', 'UNIQUE_KEY', 'UNIQUE_INDEX')) == i['is_unique'], i
        options = index.get('options', '')
        if i['include_columns']:
            assert 'INCLUDE' in options and all(c in options for c in i['include_columns']), i
        if i['predicate']:
            assert 'WHERE' in options and i['predicate'] in options, i
    for c in metadata['constraints']:
        table = tables[(database, c['table_schema'], c['table_name'])]
        if c['kind'] == 'f':
            relation = next(x for x in table.findall('fk') if x.get('name') == c['name'])
            assert relation.get('virtual') != 'y', c
            assert relation.get('to_schema') == database + '.' + c['target_schema'], c
            assert relation.get('to_table') == c['target_table'], c
            assert [x.get('name') for x in relation.findall('fk_column')] == c['columns'], c
            assert [x.get('pk') for x in relation.findall('fk_column')] == c['target_columns'], c
            if 'ON DELETE CASCADE' in c['definition']:
                assert relation.get('delete_action') == 'cascade', c
            if 'ON UPDATE CASCADE' in c['definition']:
                assert relation.get('update_action') == 'cascade', c
        if c['kind'] == 'c':
            check = next(x for x in table.findall('constraint') if x.get('name') == c['name'])
            assert check.get('type') == 'CHECK'
            assert 'ReservedPoints' in ''.join(check.itertext()), c
assert set(tables) == expected_tables
assert sum(len(t.findall('column')) for t in tables.values()) == 270
assert sum(len(t.findall('index')) for t in tables.values()) == index_count
relations = [(key, r) for key, t in tables.items() for r in t.findall('fk')]
physical = [r for _, r in relations if r.get('virtual') != 'y']
virtual = [r for _, r in relations if r.get('virtual') == 'y']
assert len(physical) == 3 and len(virtual) == 29
manifest = json.loads((ROOT / 'logical-relationships.json').read_text(encoding='utf-8'))
for r in manifest:
    table = tables[(r['database'], 'public', r['table'])]
    relation = next(x for x in table.findall('fk') if x.get('name') == r['name'])
    assert relation.get('virtual') == 'y'
    assert relation.get('to_schema') == r['target_database'] + '.public'
    assert relation.get('to_table') == r['target_table']
    assert relation.find('fk_column').get('name') == r['column']
    assert relation.find('fk_column').get('pk') == r['target_column']
assert len(model.findall('layout')) == 9
for layout in model.findall('layout')[:2]:
    groups = layout.findall('group')
    assert len(groups) == 7
    grouped = []
    for group in groups:
        database = group.get('name').split(' | ')[-1]
        for entity in group.findall('entity'):
            db, schema = entity.get('schema').rsplit('.', 1)
            assert db == database, (group.attrib, entity.attrib)
            grouped.append((db, schema, entity.get('name')))
    assert set(grouped) == expected_tables and len(grouped) == len(expected_tables)
    assert len(layout.findall('entity')) == len(expected_tables)
assert not model.findall('connector'), 'No connection credentials should be saved in the diagram'
report = {'checked_at_utc': datetime.now(timezone.utc).isoformat(), 'status': 'passed',
          'databases': 7, 'tables': 41, 'columns': 270, 'indexes': index_count,
          'physical_foreign_keys': 3, 'virtual_relationships': 29,
          'overview_service_blocks': 7, 'layouts': 9,
          'postgresql_metadata_unchanged': True,
          'checks': ['all table names and namespaces', 'all column types and nullability',
                     'varchar lengths and numeric precision', 'generated identity',
                     'all index columns and uniqueness', 'all physical FK targets and cascades',
                     'lab CHECK constraint', 'logical-reference manifest',
                     'each table grouped exactly once under its owning service',
                     'no saved database connections']}
(ROOT / 'verification.json').write_text(json.dumps(report, indent=2) + '\n', encoding='utf-8')
print(f'PASSED: 41 tables, 270 columns, {index_count} indexes, 3 real FKs, 29 virtual links; 7 complete service blocks.')
