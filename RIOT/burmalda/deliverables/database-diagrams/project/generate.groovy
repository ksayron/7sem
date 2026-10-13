import com.wisecoders.dbs.schema.*
import com.wisecoders.dbs.schema.store.ProjectStore
import com.wisecoders.dbs.schema.store.ProjectLoader
import com.wisecoders.dbs.diagram.model.Point
import javafx.scene.paint.Color
import groovy.json.JsonSlurper
import groovy.json.JsonOutput
import groovy.xml.XmlParser
import groovy.xml.XmlUtil

// Schema reads only: no SQL DDL or application-data writes.
try {
def output = new File(parameters[-1])
def snapshot = new JsonSlurper().parse(new File(output, 'schema-snapshot.json'))
Project model = new Project('Burmalda — базы всех сервисов', 'PostgreSQL')
def specs = [
    [db:'identity_db', label:'Identity · учётные записи', color:'#D9E7FA', x:80d, y:100d, cols:2,
     order:['users','refresh_tokens','outbox_messages','inbox_messages']],
    [db:'catalog_db', label:'Catalog · книги и экземпляры', color:'#CFE8E1', x:1000d, y:100d, cols:3,
     order:['books','book_copies','book_search_projection','outbox_messages','inbox_messages']],
    [db:'economy_db', label:'Economy · баллы и расчёты', color:'#F7E6BE', x:2300d, y:100d, cols:3,
     order:['wallets','point_reservations','fines','balance_transactions','outbox_messages','inbox_messages']],
    [db:'reputation_db', label:'Reputation · репутация', color:'#E9DDF5', x:80d, y:950d, cols:2,
     order:['user_reputations','reputation_adjustments','outbox_messages','inbox_messages']],
    [db:'notification_db', label:'Notifications · уведомления', color:'#F7DDE3', x:80d, y:1660d, cols:2,
     order:['notifications','outbox_messages','inbox_messages']],
    [db:'concurrency_lab_db', label:'ConcurrencyLab · учебная БД', color:'#DFE8ED', x:80d, y:2390d, cols:2,
     order:['lab_copies','lab_loans']],
    [db:'lending_db', label:'Lending · выдачи, сага и Hangfire', color:'#D8E8F5', x:1000d, y:1120d, cols:4,
     order:['loans','loan_request_sagas','loan_status_history','outbox_messages','inbox_messages']]
]
def mergedXml = new XmlParser().parseText('<project name="Burmalda — базы всех сервисов" database="PostgreSQL" id="' + UUID.randomUUID() + '"/>')
specs.each { spec ->
    Connector connection = ConnectorManager.createConnector('diagram_read_' + spec.db, 'PostgreSQL',
        'org.postgresql.Driver', 'jdbc:postgresql://127.0.0.1:5547/' + spec.db,
        System.getenv('POSTGRES_ADMIN_USER') ?: 'burmalda_admin',
        System.getenv('POSTGRES_ADMIN_PASSWORD') ?: 'burmalda_admin_dev')
    def schemaNames = snapshot.databases[spec.db].columns.collect { it.table_schema }.unique().sort()
    schemaNames.each { schemaName ->
        Project isolated = new Project(spec.db + '.' + schemaName, 'PostgreSQL')
        connection.importSchema(isolated, spec.db + '.' + schemaName)
        StringWriter xml = new StringWriter()
        new ProjectStore(isolated, xml)
        new XmlParser().parseText(xml.toString()).schema.each { mergedXml.append(it) }
        println "Imported ${spec.db}.${schemaName}: ${isolated.entityCount} tables"
    }
}
ProjectLoader mergedLoader = new ProjectLoader()
mergedLoader.parse(new ByteArrayInputStream(XmlUtil.serialize(mergedXml).getBytes('UTF-8')))
model = mergedLoader.project
def tables = [:]
def serviceTables = [:].withDefault { [] }
model.schemas.each { schema ->
        schema.tables.each { table ->
            tables[schema.catalogName + '.' + schema.name + '.' + table.name] = table
            serviceTables[schema.catalogName].add(table)
            def name = schema.name
            if (name == 'hangfire') table.comment = 'Служебная таблица планировщика Hangfire. Принадлежит Lending; не является самостоятельным сервисом.'
            else if (table.name == 'outbox_messages') table.comment = 'Исходящие сообщения этого сервиса. RabbitMQ публикует их асинхронно. Прямая FK-связь с inbox другого сервиса отсутствует.'
            else if (table.name == 'inbox_messages') table.comment = 'Дедупликация входящих сообщений по составному PK (MessageId, Consumer).'
        }
}
assert tables.size() == 41
assert tables.values().sum { it.columns.size() } == 270

def ref = { String db, String table -> tables[db + '.public.' + table] }
def logical = []
def link = { String db, String table, String column, String targetDb, String targetTable, String targetColumn ->
    def source = ref(db, table)
    def target = ref(targetDb, targetTable)
    assert source != null && target != null
    assert source.columns.getByName(column) != null && target.columns.getByName(targetColumn) != null
    String name = 'virtual_' + table + '_' + column
    ForeignKey fk = source.createRelation(name, target)
    fk.addColumns(source.columns.getByName(column), target.columns.getByName(targetColumn))
    fk.setVirtual(true)
    fk.comment = 'Логическая ссылка приложения. PostgreSQL не обеспечивает эту связь' +
        (db == targetDb ? '.' : '; ссылка пересекает границы баз сервисов.')
    source.columns.getByName(column).comment = fk.comment + ' → ' + targetDb + '.public.' + targetTable + '.' + targetColumn
    logical.add([name:name, database:db, table:table, column:column,
                 target_database:targetDb, target_table:targetTable, target_column:targetColumn])
}
link('identity_db','refresh_tokens','UserId','identity_db','users','Id')
link('catalog_db','book_copies','BookId','catalog_db','books','Id')
link('catalog_db','book_search_projection','BookId','catalog_db','books','Id')
link('catalog_db','book_copies','OwnerId','identity_db','users','Id')
link('catalog_db','book_copies','ReservedForUserId','identity_db','users','Id')
link('catalog_db','book_copies','ReservationSagaId','lending_db','loan_request_sagas','Id')
link('lending_db','loans','SagaId','lending_db','loan_request_sagas','Id')
link('lending_db','loans','CopyId','catalog_db','book_copies','Id')
link('lending_db','loans','BorrowerId','identity_db','users','Id')
link('lending_db','loans','OwnerId','identity_db','users','Id')
link('lending_db','loans','ReservationId','economy_db','point_reservations','Id')
link('lending_db','loan_request_sagas','CopyId','catalog_db','book_copies','Id')
link('lending_db','loan_request_sagas','BorrowerId','identity_db','users','Id')
link('lending_db','loan_request_sagas','OwnerId','identity_db','users','Id')
link('lending_db','loan_request_sagas','PointsReservationId','economy_db','point_reservations','Id')
link('lending_db','loan_request_sagas','LoanId','lending_db','loans','Id')
link('lending_db','loan_status_history','LoanId','lending_db','loans','Id')
link('lending_db','loan_status_history','ActorId','identity_db','users','Id')
link('economy_db','wallets','UserId','identity_db','users','Id')
link('economy_db','balance_transactions','UserId','economy_db','wallets','UserId')
link('economy_db','balance_transactions','LoanId','lending_db','loans','Id')
link('economy_db','point_reservations','UserId','economy_db','wallets','UserId')
link('economy_db','point_reservations','SagaId','lending_db','loan_request_sagas','Id')
link('economy_db','fines','UserId','economy_db','wallets','UserId')
link('economy_db','fines','LoanId','lending_db','loans','Id')
link('reputation_db','user_reputations','UserId','identity_db','users','Id')
link('reputation_db','reputation_adjustments','UserId','reputation_db','user_reputations','UserId')
link('reputation_db','reputation_adjustments','ActorId','identity_db','users','Id')
link('notification_db','notifications','UserId','identity_db','users','Id')
assert logical.size() == 29
ref('catalog_db','books').comment = 'Библиографические записи. Авторы и жанры хранятся в JSONB, отдельных таблиц нет.'
ref('catalog_db','book_search_projection').comment = 'Поисковая проекция Catalog; одна строка на BookId. Логическая связь с books, FK в БД отсутствует.'
ref('lending_db','loan_request_sagas').comment = 'Состояние саги выдачи. Соединяет резерв экземпляра, резерв баллов и созданную выдачу через сообщения сервисов.'
ref('economy_db','balance_transactions').comment = 'Журнал операций баллов; IdempotencyKey защищает от повторной обработки.'
ref('concurrency_lab_db','lab_copies').comment = 'Изолированные учебные данные. CHECK ReservedPoints >= 0. Не ссылается на реальные книги и пользователей.'

def setUp = { Layout layout ->
    layout.setShowSchemaName(true)
    layout.setShowDataType(true)
    layout.setJoinedRouting(false)
    layout.showAllForeignKeys()
}
def arrange = { Layout layout, Map spec, double x, double y, boolean compactHangfire ->
    def group = layout.createGroup(spec.label + ' | ' + spec.db)
    group.setColor(Color.web(spec.color))
    def domain = spec.order.collect { ref(spec.db, it) }
    def technical = serviceTables[spec.db].findAll { it.schema.name == 'hangfire' }.sort { it.name }
    double cursorY = y
    [domain, technical].eachWithIndex { batch, batchIndex ->
        if (!batch) return
        int count = batchIndex == 0 ? spec.cols : 4
        batch.collate(count).each { row ->
            int maxRows = 0
            row.eachWithIndex { table, index ->
                def depict = layout.attach(table)
                depict.setLocation(x + index * 390d, cursorY)
                depict.setColor(Color.web(table.schema.name == 'hangfire' ? '#E6E8EC' : spec.color))
                if (compactHangfire && table.schema.name == 'hangfire') {
                    def pk = table.primaryKey?.columns ?: []
                    table.columns.each { column -> if (!pk.contains(column)) depict.hideAttribute(column, true) }
                }
                group.attachDepict(depict)
                maxRows = Math.max(maxRows, depict.visibleAttributes.size())
            }
            cursorY += maxRows * 21d + 110d
        }
        cursorY += 80d
    }
    group.refresh()
    return cursorY
}
Layout overview = model.createLayout('01 — Обзор: блоки сервисов')
setUp(overview)
specs.each { spec -> arrange(overview, spec, spec.x, spec.y, true) }
def major = ['virtual_book_copies_OwnerId', 'virtual_loans_CopyId', 'virtual_loans_BorrowerId',
    'virtual_loans_ReservationId', 'virtual_wallets_UserId', 'virtual_fines_LoanId',
    'virtual_user_reputations_UserId', 'virtual_notifications_UserId']
logical.findAll { it.database != it.target_database && !major.contains(it.name) }.each { relation ->
    overview.hideRelation(ref(relation.database, relation.table).foreignKeys.getByName(relation.name))
}
overview.createCallout('BURMALDA · 7 баз / 41 таблица / 270 полей\nЦветной блок = сервис и принадлежащая ему БД.\nФиолетовая линия = виртуальная ссылка приложения.\nРеальных FK: 3; виртуальных ссылок: 29.\nНа обзоре показаны 8 основных межсервисных ссылок.\nПолный набор связей: вкладка 02.\nHangfire: 12 таблиц внутри Lending, показаны ключи.\nВсе поля Hangfire: вкладки 02 и Lending.\nУчебная БД изолирована от бизнес-данных.', new Point(2600d, 1510d))

Layout complete = model.createLayout('02 — Полная схема: все поля и связи')
setUp(complete)
specs.each { spec -> arrange(complete, spec, spec.x, spec.y, false) }
complete.createCallout('Полная схема текущего PostgreSQL.\n7 сервисных блоков; 41 таблица, включая Hangfire.\n29 виртуальных ссылок приложения + 3 реальных FK.\nСвязи между БД не создают JOIN-доступа для сервисов.\nПриложение использует API, события и саги.', new Point(2600d, 1510d))

specs.eachWithIndex { spec, index ->
    Layout layout = model.createLayout(String.format('%02d — %s', index + 3, spec.label))
    setUp(layout)
    arrange(layout, spec, 100d, 100d, false)
}
File modelFile = new File(output, 'burmalda-all-databases.dbs')
modelFile.withWriter('UTF-8') { writer -> new ProjectStore(model, writer) }
ProjectLoader loader = new ProjectLoader()
modelFile.withInputStream { loader.parse(it) }
def reloaded = loader.project
assert reloaded.entityCount == 41
assert reloaded.layouts.size() == 9
assert reloaded.schemas.sum { s -> s.tables.sum { t -> t.columns.size() } } == 270
assert reloaded.schemas.sum { s -> s.tables.sum { t -> t.foreignKeys.size() } } == 32
assert reloaded.layouts[0].groups.size() == 7
def expose = new Expose(new File(output, 'burmalda-all-databases.html'), model, model.layouts)
expose.put(Expose.TOC, true)
expose.put(Expose.TITLE, 'Burmalda — базы данных всех сервисов')
overview.generateHtmlDocumentation(expose)
new File(output, 'logical-relationships.json').text = JsonOutput.prettyPrint(JsonOutput.toJson(logical)) + '\n'
println 'Saved and reloaded: 41 tables, 270 columns, 32 relationships, 7 service blocks, 9 layouts.'
} catch (Throwable error) {
    error.printStackTrace()
    System.exit(1)
}
