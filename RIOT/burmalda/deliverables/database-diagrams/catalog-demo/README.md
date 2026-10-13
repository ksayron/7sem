# Catalog database diagram demo

This is the first part of a future diagram covering all Burmalda databases.
It documents the **current live `catalog_db.public` schema**, checked on
2026-10-09, with five tables, 44 columns, and nine indexes (including primary keys).
Application source and live PostgreSQL metadata were both inspected.

## Open the demo

In DbSchema, use **File → Open Model from File** and select
`burmalda-catalog-demo.dbs`. The model includes an arranged diagram, column
types, primary keys, indexes, comments, and two virtual relationships. It has
no saved connection or password and can be opened without connecting to PostgreSQL.

For a preview, open `burmalda-catalog-demo.html` in a browser, or view
`burmalda-catalog-demo.png`. The HTML contains table and column documentation
as well as the diagram. The SVG is available for scalable viewing. The preview
was exported by the installed DbSchema trial, which adds its own watermark.

## Read the diagram

| Table | Purpose |
| --- | --- |
| `books` | Bibliographic records; authors and genres are JSONB columns |
| `book_copies` | Physical copies, owner, condition, price, availability, reservation |
| `book_search_projection` | Search read model and availability summary |
| `outbox_messages` | Outgoing integration messages waiting for publication |
| `inbox_messages` | Deduplication of processed incoming messages |

The two visible relationships are **virtual**:

- `book_copies.BookId → books.Id`: one book can have zero or more copies.
- `book_search_projection.BookId → books.Id`: one book can have zero or one
  projection row; `BookId` is the projection's primary key.

The current database has **zero enforced foreign keys**. These links express
application intent; PostgreSQL can still contain an orphaned reference. No FK
was added to the running database. DbSchema supports model-only links through
[virtual foreign keys](https://dbschema.com/documentation/foreign-keys.html).

`OwnerId` and `ReservedForUserId` reference Identity users, while
`ReservationSagaId` references a Lending saga. Their targets are described in
comments and deliberately omitted from this one-database demo. There are no
invented direct relations between inbox/outbox and the domain tables.

## Extend to one combined project diagram

Keep each database in its own namespace, such as `catalog_db.public`, so repeated
names like `outbox_messages` remain distinct. Add the six other database groups:
`identity_db`, `lending_db`, `economy_db`, `reputation_db`, `notification_db`, and
the development-only `concurrency_lab_db`. Use one overview layout, plus
individual database layouts for detailed reading. Cross-database references
will be virtual links, distinct from actual PostgreSQL constraints. The databases
themselves remain separate.

This delivery only implements the Catalog demo; the combined diagram is the
next stage after review.

## Evidence and regeneration

`verification.json` contains an independent read-only comparison against
PostgreSQL: tables, all columns and types, nullability, lengths/precision,
index columns and uniqueness, and the absence of physical foreign keys.
The saved native model also passed a reload through the installed DbSchema
9.9.3 parser. The PNG was rendered from DbSchema's exported SVG and inspected.

The installed importer reports optional JSON sub-column inference warnings
because it does not quote the mixed-case column names. The real `AuthorsJson`,
`GenresJson`, and `Payload` columns are present and verified as JSONB. The model
does not contain inferred JSON sub-columns or sampled application records.

From PowerShell, regenerate with the local installation:

```powershell
$demoDir = 'D:\BSTU\7sem\RIOT\burmalda\deliverables\database-diagrams\catalog-demo'
Push-Location 'D:\Soft\DbSchema'
try {
    & '.\jre\bin\java.exe' -cp '.\lib\*' com.wisecoders.dbs.DbSchema -x "$demoDir\generate.groovy" $demoDir
} finally {
    Pop-Location
}
& 'C:\Users\user\.cache\codex-runtimes\codex-primary-runtime\dependencies\python\python.exe' "$demoDir\verify.py"
```

Generation reads local schema metadata with `catalog_app`, using the existing
development password by default or `CATALOG_DB_PASSWORD` if set. Verification
uses `docker exec` against the local container. Neither process performs DDL or
changes application data. Preview PNG/SVG files are snapshots of the HTML export.
