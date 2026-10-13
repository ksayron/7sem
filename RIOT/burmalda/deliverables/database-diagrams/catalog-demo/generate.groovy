import com.wisecoders.dbs.schema.*
import com.wisecoders.dbs.schema.store.ProjectStore
import com.wisecoders.dbs.schema.store.ProjectLoader
import com.wisecoders.dbs.diagram.model.Point
import javafx.scene.paint.Color
import java.nio.charset.StandardCharsets

// Reads schema metadata only. Does not execute DDL or change application data.
def output = new File(parameters.size() > 1 ? parameters[-1] : '.')
output.mkdirs()
Project model = new Project('Burmalda - catalog_db demo', 'PostgreSQL')
Connector connection = ConnectorManager.createConnector(
    'burmalda_catalog_diagram_read', 'PostgreSQL', 'org.postgresql.Driver',
    'jdbc:postgresql://127.0.0.1:5547/catalog_db', 'catalog_app',
    System.getenv('CATALOG_DB_PASSWORD') ?: 'catalog_dev')
connection.setPassword(System.getenv('CATALOG_DB_PASSWORD') ?: 'catalog_dev')
Schema schema = connection.importSchema(model, 'catalog_db.public')
assert schema != null
assert schema.tables.size() == 5
println "Imported ${schema.nameWithCatalog}: ${schema.tables.collect { it.name }}"

def comments = [
    books: 'Bibliographic records. Authors and genres are stored as JSONB values, not separate tables.',
    book_copies: 'Physical copies and lending terms. BookId is a logical reference to books.Id; PostgreSQL does not enforce an FK. OwnerId and ReservedForUserId identify Identity users. ReservationSagaId identifies a Lending saga; these cross-database references are application-level only.',
    book_search_projection: 'Replaceable search read model, maintained by Catalog. BookId logically refers to books.Id; no PostgreSQL FK exists.',
    outbox_messages: 'Persisted outgoing integration messages. Published asynchronously to RabbitMQ; no table-level foreign key to domain records.',
    inbox_messages: 'Deduplication of incoming messages by the composite primary key (MessageId, Consumer). No declared FK to the outbox.'
]
schema.tables.each { table -> table.comment = comments[table.name] }
def books = schema.tables.getByName('books')
def copies = schema.tables.getByName('book_copies')
def search = schema.tables.getByName('book_search_projection')
copies.columns.getByName('Condition').comment = 'Stored integer enum: 0 New, 1 Excellent, 2 Good, 3 Used, 4 Poor.'
copies.columns.getByName('Status').comment = 'Stored integer enum: 0 Available, 1 Reserved, 2 OnLoan, 3 Unavailable.'
copies.columns.getByName('Version').comment = 'Application optimistic concurrency token; bigint, not a PostgreSQL-generated row version.'
copies.columns.getByName('OwnerId').comment = 'Logical cross-database reference to Identity user; target omitted in this Catalog-only demo.'
copies.columns.getByName('ReservedForUserId').comment = 'Optional logical cross-database reference to Identity user.'
copies.columns.getByName('ReservationSagaId').comment = 'Optional logical cross-database reference to Lending saga.'
[
    [copies, 'virtual_book_copies_book', 'BookId'],
    [search, 'virtual_search_projection_book', 'BookId']
].each { spec ->
    ForeignKey relation = spec[0].createRelation(spec[1], books)
    relation.addColumns(spec[0].columns.getByName(spec[2]), books.columns.getByName('Id'))
    relation.setVirtual(true)
    relation.setLogicalMandatory(true)
    relation.setRelationCardinality(RelationCardinality.values().find {
        it.toString() == (spec[0] == search ? 'ZeroOne' : 'ZeroMore')
    })
    relation.comment = 'Logical relationship only. Not an enforced PostgreSQL foreign key.'
}

Layout layout = model.createLayout('Catalog - tables and logical relationships')
layout.setShowSchemaName(true)
layout.setShowDataType(true)
layout.showAllForeignKeys()
layout.setJoinedRouting(false)
def positions = [books: [540d, 50d], book_copies: [50d, 50d],
    book_search_projection: [1030d, 50d], outbox_messages: [540d, 440d], inbox_messages: [1030d, 440d]]
schema.tables.each { table ->
    def depict = layout.attach(table)
    depict.setLocation(*positions[table.name])
    if (table.name == 'book_copies') depict.setSize(330, 330)
    depict.setColor(Color.web(table.name.endsWith('_messages') ? '#DDE5EF' : '#CFE8E1'))
}
layout.createCallout('CATALOG_DB / public\nLive PostgreSQL schema: 5 tables.\nTwo virtual book relationships; no physical foreign keys.\nPK = primary key; nullable columns are optional.\nInbox/outbox have no invented table relationships.\nExternal Identity/Lending references are in column comments.', new Point(50d, 440d))

File modelFile = new File(output, 'burmalda-catalog-demo.dbs')
modelFile.withWriter('UTF-8') { writer -> new ProjectStore(model, writer) }
// Verify that the installed DbSchema parser can reload the native file.
ProjectLoader loader = new ProjectLoader()
modelFile.withInputStream { stream -> loader.parse(stream) }
Project reloaded = loader.getProject()
assert reloaded.schemas[0].tables.size() == 5
assert reloaded.schemas[0].tables.sum { it.columns.size() } == 44
assert reloaded.schemas[0].tables.sum { it.foreignKeys.size() } == 2
println "Reloaded model: ${reloaded.entityCount} entities; 44 columns; 2 virtual relationships."
layout.generateHtmlDocumentation(new File(output, 'burmalda-catalog-demo.html'))
println "Saved model and HTML documentation to ${output.canonicalPath}"
