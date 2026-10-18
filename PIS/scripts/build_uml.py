"""Build editable target-state UML for PIS labs 4-6, then render saved XML.

Run with Python 3 and Pillow: python scripts/build_uml.py
All elements, compartments, endpoints, labels and waypoints remain editable.
The class model is a design model, not a generated database or code inventory.
"""
from pathlib import Path
import copy
import hashlib
import html
import json
import math
import re
import textwrap
import xml.etree.ElementTree as ET
from PIL import Image, ImageDraw, ImageFont
import use_cases

ROOT = Path(__file__).resolve().parents[1]
PREVIEWS = ROOT / 'uml-previews'
FONT = 'C:/Windows/Fonts/times.ttf'
BOLD = 'C:/Windows/Fonts/timesbd.ttf'
CATALOG = []


def mxfile():
    return ET.Element('mxfile', host='app.diagrams.net', compressed='false', type='device')


def save(root, path):
    root.set('pages', str(len(root.findall('diagram'))))
    ET.indent(root, space='  ')
    path.write_bytes(ET.tostring(root, encoding='utf-8', xml_declaration=True))


class Page:
    def __init__(self, file, slug, title, subtitle, w=1800, h=1280):
        self.slug, self.w, self.h = slug, w, h
        self.nodes, self.counter = {}, 0
        self.diagram = ET.SubElement(file, 'diagram', id=slug, name=title)
        model = ET.SubElement(self.diagram, 'mxGraphModel', dx=str(w), dy=str(h),
            grid='1', gridSize='10', guides='1', tooltips='1', connect='1', arrows='1',
            fold='1', page='1', pageScale='1', pageWidth=str(w), pageHeight=str(h),
            math='0', shadow='0')
        self.root = ET.SubElement(model, 'root')
        ET.SubElement(self.root, 'mxCell', id='0')
        ET.SubElement(self.root, 'mxCell', id='1', parent='0')
        self.box('title', title, 35, 18, w-70, 44, 'text', 30, True)
        self.box('subtitle', subtitle, 35, 65, w-70, 45, 'text', 21)
        self.box('footer', 'Sensei++  |  UML: проектная модель «как будет»  |  ПИС · лабораторные работы 4-6',
                 35, h-47, w-70, 30, 'text', 18)

    def box(self, ident, value, x, y, w, h, kind='rect', size=22,
            bold=False, parent='1', **extra):
        styles = {
            'text': 'text;strokeColor=none;fillColor=none;align=center;',
            'note': 'shape=note;align=left;spacing=12;',
            'package': 'shape=folder;tabWidth=150;tabHeight=30;align=center;verticalAlign=middle;',
            'class': 'swimlane;horizontal=1;startSize=42;collapsible=0;align=center;',
            'attrs': 'text;strokeColor=none;fillColor=none;align=left;spacingLeft=12;verticalAlign=top;spacingTop=9;',
            'methods': 'shape=partialRectangle;top=1;left=0;right=0;bottom=0;fillColor=none;align=left;spacingLeft=12;verticalAlign=top;spacingTop=9;',
            'component': 'shape=module;align=center;spacingLeft=20;',
            'node': 'shape=cube;size=16;direction=south;align=center;verticalAlign=top;spacingTop=23;',
            'frame': 'rounded=0;align=center;verticalAlign=top;spacingTop=14;fillColor=none;',
            'runtime': 'rounded=0;align=center;verticalAlign=top;spacingTop=12;',
            'artifact': 'rounded=0;align=center;',
            'interface': 'rounded=0;align=center;',
            'rect': 'rounded=0;align=center;',
        }
        style = (f'whiteSpace=wrap;html=0;fontFamily=Times New Roman;fontSize={size};'
                 'fontColor=#000000;fontStyle=0;fillColor=#FFFFFF;strokeColor=#000000;strokeWidth=1.2;'
                 + styles[kind])
        if bold:
            style += 'fontStyle=1;'
        attrs = dict(id=ident, value=value, style=style, vertex='1', parent=parent, kind=kind)
        attrs.update({k:str(v) for k,v in extra.items()})
        cell = ET.SubElement(self.root, 'mxCell', **attrs)
        ET.SubElement(cell, 'mxGeometry', x=str(x), y=str(y), width=str(w), height=str(h), **{'as':'geometry'})
        px, py = (0, 0) if parent == '1' else self.nodes[parent][:2]
        self.nodes[ident] = (px+x, py+y, w, h, cell)
        return ident

    def klass(self, ident, name, attrs, methods, x, y, w=420, external=False):
        ah = max(43, 18+len(attrs)*27)
        mh = max(43, 18+len(methods)*27)
        h = 42+ah+mh
        self.box(ident, name, x,y,w,h,'class',23,True, className=name, external=str(external).lower())
        self.box(ident+'-a', '\n'.join(attrs),0,42,w,ah,'attrs',21,parent=ident)
        self.box(ident+'-m', '\n'.join(methods),0,42+ah,w,mh,'methods',21,parent=ident)
        CATALOG.append(dict(page=self.slug, name=name, attributes=attrs, operations=methods, external=external))
        return ident

    def edge(self, source, target, label='', kind='dependency', sides=(1,.5,0,.5), points=None,
             mult=None, label_offset=(0,-15), cardinal_offset=None, label_pos=None):
        self.counter += 1
        ident = self.slug+'-e'+str(self.counter)
        sx,sy,tx,ty = sides
        a,b=self.nodes[source],self.nodes[target]
        horizontal_classes = (a[4].get('kind')==b[4].get('kind')=='class'
                              and abs(a[1]-b[1])<2 and not points)
        if horizontal_classes:
            sy,ty=100/a[3],100/b[3]
            gap=abs((b[0] if sx==1 else a[0])-(a[0]+a[2] if sx==1 else b[0]+b[2]))
            ff=ImageFont.truetype(FONT,20)
            words=label.split(); ls=[]; cur=''
            for word in words:
                trial=(cur+' '+word).strip()
                if cur and ff.getlength(trial)>gap-24: ls.append(cur);cur=word
                else:cur=trial
            if cur:ls.append(cur)
            label='\n'.join(ls);label_offset=(0,-30)
        elif a[4].get('kind')==b[4].get('kind')=='class' and not points and abs(a[0]-b[0])<2:
            label_offset=(120,0)
        end = 'open' if kind in ('dependency','navigation') else 'block' if kind in ('generalization','realization') else 'none'
        start = 'diamond' if kind=='composition' else 'none'
        st = (f'edgeStyle=none;rounded=0;html=0;fontFamily=Times New Roman;fontSize=20;'
              f'strokeColor=#000000;strokeWidth=1.2;endArrow={end};endFill=0;'
              f'startArrow={start};startFill=1;startSize=13;endSize=13;'
              f'exitX={sx};exitY={sy};entryX={tx};entryY={ty};exitPerimeter=0;entryPerimeter=0;'
              'labelBackgroundColor=#FFFFFF;')
        if kind in ('dependency','realization','manifest','deploy'):
            st += 'dashed=1;dashPattern=6 4;'
        if kind in ('manifest','deploy'):
            st = st.replace('endArrow=none','endArrow=open')
        c = ET.SubElement(self.root,'mxCell',id=ident,value=label,style=st,edge='1',parent='1',
                          source=source,target=target,relation=kind)
        g = ET.SubElement(c,'mxGeometry', relative='1', **{'as':'geometry'})
        pts=[(a[0]+a[2]*sx,a[1]+a[3]*sy)]+(points or [])+[(b[0]+b[2]*tx,b[1]+b[3]*ty)]
        if label_pos:
            center=along(pts,.5);label_offset=(label_pos[0]-center[0],label_pos[1]-center[1])
        ET.SubElement(g,'mxPoint',x=str(label_offset[0]),y=str(label_offset[1]),**{'as':'offset'})
        if points:
            arr=ET.SubElement(g,'Array',**{'as':'points'})
            for x,y in points:
                ET.SubElement(arr,'mxPoint',x=str(x),y=str(y))
        if mult:
            total=sum(math.dist(pa,pb) for pa,pb in zip(pts,pts[1:]))
            near=min(32/total,.15) if total else .1
            for i, value in enumerate(mult):
                p,q=(pts[0],pts[1]) if i==0 else (pts[-1],pts[-2])
                off=(0,18) if horizontal_classes else (0,-16) if abs(q[0]-p[0])>=abs(q[1]-p[1]) else (23,0)
                if cardinal_offset: off=cardinal_offset[i]
                lab=ET.SubElement(self.root,'mxCell', id=ident+f'-mult{i}', value=value,
                    style='edgeLabel;html=0;align=center;verticalAlign=middle;fontFamily=Times New Roman;fontSize=20;labelBackgroundColor=#FFFFFF;',
                    vertex='1',connectable='0',parent=ident,kind='multiplicity')
                gg=ET.SubElement(lab,'mxGeometry',x=str(-1+2*near if i==0 else 1-2*near),y='0',relative='1',**{'as':'geometry'})
                ET.SubElement(gg,'mxPoint',x=str(off[0]),y=str(off[1]),**{'as':'offset'})
        return ident

    def note(self, text, y, h=100):
        return self.box('note-'+str(y),text,45,y,self.w-90,h,'note',21)


def build_structural():
    f=mxfile()
    p=Page(f,'packages','07 Диаграмма пакетов','ЛР5 · логические пакеты и зависимости; стрелка направлена от потребителя к используемому контракту')
    packs=[('identity','Identity\nУчётные записи и приватность',70,180),
           ('learning','Learning\nКонтент, практика и маршруты',70,470),
           ('work','WorkReflection\nЭпизоды и осмысление работы',70,760),
           ('experience','Experience\nРедакции опыта и представления',710,180),
           ('evidence','Evidence\nСвидетельства и профиль',710,470),
           ('sync','ClientSync\nПакеты и повтор операций',710,760),
           ('shared','Shared.Contracts\nВладелец, ID, версии, порты',1350,180),
           ('infra','Infrastructure\nEF Core, ИИ, задания, метрики',1350,760)]
    for id,label,x,y in packs:p.box(id,label,x,y,380,145,'package',24)
    p.edge('learning','evidence','история / наблюдения')
    p.edge('work','evidence','наблюдения',points=[(480,832),(480,680),(670,680),(670,542)],label_pos=(585,645))
    p.edge('experience','evidence','проверяемые\nисточники',sides=(.5,1,.5,0),label_pos=(1050,360))
    p.edge('experience','work','контекст эпизода',sides=(0,.5,1,.25),points=[(510,252),(510,796)])
    p.edge('sync','learning','учебные команды',sides=(0,.25,1,1),points=[(620,796),(620,725),(450,725)],label_pos=(535,755))
    p.edge('learning','shared','общие контракты',sides=(.5,0,.5,1),points=[(260,400),(1540,400)])
    p.edge('identity','shared','контекст владельца',points=[(490,252),(490,137),(1540,137)],sides=(1,.5,.5,0))
    p.edge('evidence','shared','ID и версии',points=[(1225,542),(1225,252)])
    p.edge('infra','shared','реализует порты',sides=(.5,0,.5,1))
    p.note('Пять предметных модулей работают внутри единого сервера. Показаны основные зависимости контрактов.\nКаждый модуль: Api → Application → Domain; Infrastructure → Application/Domain. Host связывает адаптеры.\nClientSync — целевой пакет. Межмодульные порты подключаются в Host; прямой записи в чужие таблицы нет.',1020,150)

    p=Page(f,'content','08 Классы: контент и учебные цели','ЛР5 · − закрытый атрибут; + открытая операция; кратности указаны у концов связей',h=1500)
    p.klass('concept','Concept',['− id: Guid','− title: String','− difficulty: Difficulty'],['+ rename(title): void'],70,170)
    p.klass('relation','ConceptRelation',['− sourceId: Guid','− targetId: Guid','− kind: RelationKind'],['+ create(source, target): Relation'],690,170)
    p.klass('material','MaterialVersion',['− id: Guid','− locale: String','− content: ContentBlock[]','− available: Boolean'],['+ deactivate(): void'],1310,170)
    p.klass('goal','LearningGoal',['− ownerId: Guid','− title: String','− status: GoalStatus'],['+ setTarget(conceptId): void'],70,615)
    p.klass('exercise','ExerciseVersion',['− id: Guid; revision: Int','− type: ExerciseType','− locale: String','− evaluation: EvaluationDefinition'],['+ validate(): ValidationResult','+ deactivate(): void'],690,615)
    p.klass('family','ExerciseFamily',['− id: Guid','− key: String'],['+ addVersion(): ExerciseVersion'],1310,615)
    p.klass('roadmap','RoadmapVersion',['− id: Guid','− revision: Int','− title: String'],['+ publish(): void'],70,1040)
    p.klass('stage','RoadmapStage',['− order: Int','− title: String','− conceptIds: Guid[]'],['+ reorder(position): void'],690,1040)
    p.edge('relation','concept','source / target',kind='association',mult=('0..*','1'),sides=(0,.5,1,.5),label_offset=(0,-45))
    p.edge('concept','material','основная тема',kind='association',mult=('1','0..*'),sides=(.5,0,.5,0),points=[(280,132),(1520,132)])
    p.edge('concept','exercise','основная тема',kind='association',mult=('1','0..*'),sides=(1,1,.5,0),points=[(575,356),(575,555),(900,555)])
    p.edge('goal','concept','цели по понятиям',kind='association',mult=('0..*','1..*'),sides=(.5,0,.5,1))
    p.edge('family','exercise','версии',kind='association',mult=('1','1..*'),sides=(0,.5,1,.5))
    p.edge('exercise','material','справочный\nматериал',kind='association',mult=('0..*','0..1'),sides=(1,.2,.5,1),points=[(1200,663),(1200,450),(1520,450)],label_pos=(1380,485))
    p.edge('roadmap','stage','этапы',kind='composition',mult=('1','1..*'))
    p.edge('stage','concept','общие понятия',kind='association',mult=('0..*','1..*'),sides=(1,.5,0,.5),points=[(1180,1133),(1180,980),(35,980),(35,263)])
    p.note('ExerciseType: Flashcard, Prediction, MultipleChoice, Ordering, Matching, BugSpotting.\nОпубликованное содержимое версии неизменно; Available меняет только доступность для новых назначений.\nRelationKind: PartOf, Prerequisite, RelatedTo. Цель и маршрут ссылаются на общие понятия, не копируют прогресс.',1360,90)

    p=Page(f,'practice-evidence','09 Классы: практика и свидетельства','ЛР5 · чёрный ромб находится у целого; источники и условия свидетельства сохраняются отдельно',h=1500)
    p.klass('user','UserAccount «ref»',['− id: Guid'],['+ authorize(resource): Boolean'],70,175,external=True)
    p.klass('session','LearningSession',['− id: Guid; ownerId: Guid','− state: SessionState','− mode: LearningMode'],['+ pause(): void','+ complete(): void'],690,175)
    p.klass('exercise','ExerciseVersion «ref»',['− id: Guid; revision: Int'],['+ validate(): ValidationResult'],1310,175,external=True)
    p.klass('observation','EvidenceObservation',['− ownerId: Guid; conceptId: Guid','− sourceKind: SourceKind','− sourceId: Guid; assistance: Level','− conditions: String; status: Status'],['+ dispute(): void','+ withdraw(): void'],70,605)
    p.klass('attempt','LearningAttempt',['− id: Guid; operationId: Guid','− answer: Answer','− submittedAt: Instant'],['+ submit(): ExerciseResult'],690,605)
    p.klass('item','SessionItem',['− id: Guid; order: Int','− exerciseVersionId: Guid','− outcome: ItemOutcome'],['+ skip(): void'],1310,605)
    p.klass('profile','ProfileProjection',['− ownerId: Guid','− conceptId: Guid','− calculatedAt: Instant'],['+ rebuild(observations): void'],70,1080)
    p.klass('result','ExerciseResult',['− kind: ResultKind','− score: Decimal?','− evaluatorVersion: String'],['+ explain(): String'],690,1080)
    p.klass('help','AssistanceEvent',['− kind: AssistanceKind','− occurredAt: Instant'],['+ record(): void'],1310,1080)
    p.edge('user','session','владелец',kind='association',mult=('1','0..*'))
    p.edge('session','item','элементы',kind='composition',mult=('1','1..*'),sides=(.75,1,0,.25),points=[(1005,480),(1210,480),(1210,651.5)],label_pos=(1110,452))
    p.edge('item','exercise','назначенная версия',kind='association',mult=('0..*','1'),sides=(.5,0,.5,1))
    p.edge('item','attempt','попытки',kind='composition',mult=('1','0..*'),sides=(0,.6,1,.6))
    p.edge('attempt','result','результат',kind='composition',mult=('1','0..1'),sides=(.5,1,.5,0))
    p.edge('attempt','help','помощь',kind='composition',mult=('1','0..*'),sides=(1,.9,.5,0),points=[(1180,772.4),(1180,980),(1520,980)])
    p.edge('observation','result','источник\nрезультата',kind='association',mult=('0..*','0..1'),sides=(1,.5,0,.5),points=[(580,725),(580,1173)],label_pos=(580,980))
    p.edge('profile','observation','производный профиль',sides=(.5,0,.5,1))
    p.note('Пользователь также владеет EvidenceObservation; Concept связан с каждым наблюдением (1 → 0..*).\nИсточник результата необязателен: наблюдение может происходить из опыта или разрешённого импорта.\nСамооценка, объективный результат и необязательная ИИ-обратная связь различаются по ResultKind.',1360,90)

    p=Page(f,'work-experience','10 Классы: рабочие эпизоды и опыт','ЛР5 · подтверждение контекста, одобрение опыта и просмотр представления — отдельные решения',h=1500)
    p.klass('episode','WorkEpisode',['− id: Guid; ownerId: Guid','− context: String','− contribution: String','− archived: Boolean'],['+ reviseContext(): ContextRevision','+ archive(): void'],70,170)
    p.klass('reflection','ReflectionSession',['− id: Guid; ownerId: Guid','− contextRevisionId: Guid','− openQuestions: String[]'],['+ discuss(): void'],690,170)
    p.klass('ack','ReviewAcknowledgement',['− contextRevisionId: Guid','− discussionRevisionId: Guid','− acknowledgedBy: Guid','− acknowledgedAt: Instant'],['+ recordExactRevision(): void'],1310,170)
    p.klass('entry','ExperienceEntry',['− id: Guid; ownerId: Guid','− sourceEpisodeId: Guid?'],['+ createRevision(): ExperienceRevision'],70,625)
    p.klass('revision','ExperienceRevision',['− id: Guid; revision: Int','− context: String; actions: String','− contribution: String','− outcome: String'],['+ createSuccessor(): Revision'],690,625)
    p.klass('approval','ExperienceApproval',['− revisionId: Guid','− ownerId: Guid','− contentHash: String','− approvedAt: Instant'],['+ approveExactRevision(): void'],1310,625)
    p.klass('evidence','EvidenceObservation «ref»',['− id: Guid','− sourceId: Guid; status: Status'],['+ dispute(): void'],70,1115,external=True)
    p.klass('story','PresentationRevision',['− id: Guid; content: String','− experienceRevisionId: Guid'],['+ createSuccessor(): Revision','+ exportReviewed(): File'],690,1115)
    p.klass('review','PresentationReview',['− presentationRevisionId: Guid','− ownerId: Guid','− reviewedAt: Instant'],['+ reviewExactRevision(): void'],1310,1115)
    p.edge('episode','reflection','обсуждения',kind='composition',mult=('1','0..*'))
    p.edge('reflection','ack','рассмотренная версия',kind='association',mult=('1','0..*'))
    p.edge('entry','episode','необязательный источник',kind='association',mult=('0..*','0..1'),sides=(.5,0,.5,1))
    p.edge('entry','revision','редакции',kind='composition',mult=('1','1..*'))
    p.edge('revision','approval','одобрение владельца',kind='association',mult=('1','0..1'))
    p.edge('revision','story','основание рассказа',kind='association',mult=('1','0..*'),sides=(.5,1,.5,0))
    p.edge('story','review','просмотр версии',kind='association',mult=('1','0..1'))
    p.edge('revision','evidence','свидетельства',kind='association',mult=('0..*','0..*'),sides=(0,1,.5,0),points=[(580,953),(280,953)])
    p.note('Все личные объекты проверяются по OwnerId. Опыт можно создать без WorkEpisode.\nПравка фактов создаёт новую редакцию без прежнего одобрения. ИИ создаёт только необязательный черновик.\nЭкспорт требует ExperienceApproval исходной редакции и PresentationReview точной версии рассказа.',1360,90)

    p=Page(f,'identity-sync','11 Классы: доступ и автономная работа','ЛР5 · целевая модель пакетов, локальных операций и серверных подтверждений',h=1450)
    p.klass('user','UserAccount',['− id: Guid; email: String','− status: AccountStatus','− locale: String'],['+ changeProfile(): void','+ deactivate(): void'],70,170)
    p.klass('role','Role',['− id: Guid','− name: RoleName'],['+ allows(action): Boolean'],690,170)
    p.klass('privacy','PrivacySettings',['− ownerId: Guid','− allowedContext: String[]'],['+ grant(scope): void','+ revoke(scope): void'],1310,170)
    p.klass('device','ClientDevice',['− id: Guid; ownerId: Guid','− changeCursor: String'],['+ register(): void'],70,640)
    p.klass('operation','LocalOperation',['− operationId: Guid','− resourceId: Guid','− expectedVersion: String','− payload: Json; state: SyncState'],['+ queue(): void','+ retrySameId(): void'],690,640)
    p.klass('receipt','OperationReceipt',['− ownerId: Guid','− operationId: Guid','− payloadHash: String','− status: ReceiptStatus'],['+ replayResult(): Json'],1310,640)
    p.klass('pack','OfflinePackage',['− id: Guid; version: Int','− contentHash: String','− manifest: VersionRef[]'],['+ verifyIntegrity(): Boolean','+ activate(): void'],70,1070)
    p.edge('user','role','назначенные роли',kind='association',mult=('0..*','1..*'))
    p.edge('user','privacy','настройки',kind='composition',mult=('1','1'),sides=(.5,0,.5,0),points=[(280,135),(1520,135)])
    p.edge('user','device','владелец',kind='association',mult=('1','0..*'),sides=(.5,1,.5,0))
    p.edge('device','operation','очередь',kind='composition',mult=('1','0..*'))
    p.edge('operation','receipt','серверный ответ',kind='association',mult=('1','0..1'))
    p.edge('device','pack','загруженные пакеты',kind='association',mult=('0..*','0..*'),sides=(.5,1,.5,0))
    p.box('sync-rules','Правила синхронизации\n\nПовтор: тот же OperationId и PayloadHash.\nКонфликт версии: данные сохраняются до выбора.\nЛокальная операция может не иметь чека.\nПакет ссылается на закреплённые версии.\nКлюч чека: (OwnerId, OperationId).',690,1080,1040,215,'note',23)
    p.note('RoleName: Developer, ContentAuthor, SystemAdministrator. Роли не являются подклассами UserAccount.\nАвтономный контур относится к целевой системе; загрузка и синхронизация требуют соединения с сервером.',1315,75)
    save(f,ROOT/'lab5.drawio')


def build_physical():
    f=mxfile()
    p=Page(f,'components','12 Диаграмма компонентов','ЛР6 · единый модульный сервер; пунктирная стрелка — использование интерфейса или компонента',w=1900,h=1550)
    p.box('web','Веб-клиент / PWA\nReact + TypeScript',60,180,400,125,'component',25)
    p.box('local','Автономное хранилище\nIndexedDB: пакеты и операции',60,450,400,125,'component',24)
    p.box('agent','Локальный агент (опционально)\n.NET / выбранный контекст',60,790,400,125,'component',24)
    p.box('server','Сервер Sensei++ · ASP.NET Core · один процесс / одна поставка',580,135,1250,1060,'frame',26,True)
    p.box('api','«interface»\nHTTPS JSON API /api/v1',50,65,1120,80,'interface',24,parent='server')
    p.box('identity','Identity\nВход, роли, приватность',50,210,310,120,'component',23,parent='server')
    p.box('learning','Learning\nКонтент, практика, проверка',440,210,310,120,'component',23,parent='server')
    p.box('work','WorkReflection\nКонтекст и обсуждения',830,210,310,120,'component',23,parent='server')
    p.box('sync','ClientSync (целевой)\nПакеты, повторы, конфликты',50,490,310,120,'component',22,parent='server')
    p.box('evidence','Evidence\nНаблюдения и профиль',440,490,310,120,'component',23,parent='server')
    p.box('experience','Experience\nОпыт и представления',830,490,310,120,'component',23,parent='server')
    p.box('persist','Persistence\nEF Core / Npgsql / Unit of Work',50,820,510,120,'component',23,parent='server')
    p.box('ai','Задания и адаптер ИИ (опц.)\nBackgroundService / HTTP client',680,820,460,120,'component',23,parent='server')
    p.box('db','PostgreSQL\nДанные модулей и чеки операций',680,1260,500,110,'component',24)
    p.box('provider','Внешний ИИ-провайдер\nтолько разрешённый контекст',1350,1260,480,110,'component',24)
    p.edge('web','api','HTTPS / JSON',kind='dependency',points=[(500,242),(500,240)])
    p.edge('web','local','локальный API',sides=(.5,1,.5,0))
    p.edge('agent','api','HTTPS / выбранные\nсобытия',points=[(520,852),(520,285),(610,285)],sides=(1,.5,0,1),label_pos=(340,680))
    for target in ['identity','learning','work']:
        n=p.nodes[target];p.edge('api',target,'',sides=(.5,1,.5,0),points=[(1190,320),(n[0]+n[2]/2,320)])
    p.edge('learning','evidence','IEvidenceWriter',sides=(.5,1,.5,0))
    p.edge('work','experience','IExperienceDraft',sides=(.5,1,.5,0))
    p.edge('experience','evidence','IEvidenceReader',sides=(0,.5,1,.5),label_pos=(1370,585))
    p.edge('sync','learning','ILearningCommands',sides=(.5,0,0,.6),points=[(785,565),(970,565),(970,417)])
    p.edge('evidence','persist','IRepository / IUnitOfWork',sides=(.5,1,.5,0),points=[(1175,840),(885,840)])
    p.edge('experience','ai','IDraftGenerator',sides=(.5,1,.5,0))
    p.edge('persist','db','Npgsql / SQL',sides=(.5,1,.5,0))
    p.edge('ai','provider','HTTPS / JSON (опц.)',sides=(.5,1,.5,0))
    p.box('legend','API выполняет авторизацию через Identity. Persistence обслуживает все модули; показана одна связь для читаемости.\nМежмодульные контракты работают в процессе. Автономность и ИИ — целевые расширения; базовая проверка ответов не требует ИИ.',50,1410,1800,70,'text',20)

    p=Page(f,'deployment','13 Диаграмма развёртывания','ЛР6 · вариант размещения для небольшой группы; характеристики сервера предварительные',w=1900,h=1560)
    p.box('client','«device»\nКомпьютер / смартфон пользователя',70,170,560,770,'node',25)
    p.box('browser','«executionEnvironment»\nСовременный веб-браузер',40,115,470,190,'runtime',23,parent='client')
    p.box('spa','«artifact»\nReact SPA / PWA bundle',35,85,400,70,'artifact',23,parent='browser')
    p.box('idb','«executionEnvironment»\nIndexedDB / Cache Storage',40,340,470,115,'runtime',23,parent='client')
    p.box('agent-runtime','«executionEnvironment»\n.NET 10 · только ПК (опц.)',40,525,470,185,'runtime',23,parent='client')
    p.box('agent-exe','«artifact»\nSensei.LocalAgent',25,95,420,65,'artifact',22,parent='agent-runtime')
    p.box('server','«device» Сервер приложений и БД\n4 ядра · 16 ГБ RAM · ≥20 ГБ свободно',820,170,1000,850,'node',25)
    p.box('dotnet','«executionEnvironment»\n.NET 10 / ASP.NET Core (Kestrel)',50,135,880,295,'runtime',24,parent='server')
    p.box('host','«artifact» Sensei.Host\nAPI + Identity + Learning + WorkReflection\nEvidence + Experience + ClientSync (целевой)',40,85,800,120,'artifact',24,parent='dotnet')
    p.box('webdist','«artifact»\nСтатический клиент (wwwroot)',40,220,390,60,'artifact',21,parent='dotnet')
    p.box('pg','«executionEnvironment»\nPostgreSQL 18',50,525,880,245,'runtime',24,parent='server')
    p.box('database','«artifact»\nБаза Sensei: схемы модулей, задания, чеки',40,90,800,100,'artifact',24,parent='pg')
    p.box('backup','«device» Отдельное хранилище\nСуществующий диск / сетевой ресурс',70,1090,560,270,'node',24)
    p.box('dump','«artifact»\nЗашифрованная резервная копия',40,115,470,95,'artifact',24,parent='backup')
    p.box('external','«external device»\nОпциональный ИИ-провайдер',1190,1130,630,230,'node',24)
    p.box('ai-service','«executionEnvironment»\nHTTPS API',45,105,540,80,'runtime',23,parent='external')
    p.edge('client','server','HTTPS / TLS · TCP 443\nSPA + JSON /api/v1',kind='association',sides=(1,.25,0,192.5/850),label_offset=(0,-35))
    p.edge('host','database','SQL / Npgsql · TCP 5432\nлокальное соединение',kind='association',sides=(.75,1,.75,0),label_offset=(150,0))
    p.edge('browser','idb','локальный browser API',kind='association',sides=(.5,1,.5,0),label_offset=(145,0))
    p.edge('server','backup','pg_dump → шифрование → копия\nфайловый ввод-вывод / SMB 3',kind='association',sides=(0,.8,1,.45),points=[(720,850),(720,1212)],label_offset=(-170,0))
    p.edge('server','external','HTTPS / TLS · TCP 443 (опц.)',kind='association',sides=(.7,1,.5,0),label_offset=(160,0))
    p.box('notes','Сервер и клиент могут работать на одном учебном компьютере. БД не публикуется клиентам.\nКопия — не реже 1 раза в сутки; целевые RPO ≤ 24 ч и RTO ≤ 2 ч требуют проверки восстановлением.\nОС сервера: Windows или Linux. Автономные пакеты требуют свободного места на клиенте; GPU для базового обучения не нужен.',45,1400,1810,95,'note',21)
    save(f,ROOT/'lab6.drawio')


def styles(c):
    return dict(p.split('=',1) for p in c.get('style','').split(';') if '=' in p)


def plain(value):
    return html.unescape(re.sub('<[^>]+>','',value.replace('<br>','\n')))


def edge_points(c, nodes):
    s=styles(c);a,b=nodes[c.get('source')],nodes[c.get('target')]
    return [(a[0]+a[2]*float(s.get('exitX',1)), a[1]+a[3]*float(s.get('exitY',.5)))]+[
        (float(n.get('x')),float(n.get('y'))) for n in c.findall('mxGeometry/Array/mxPoint')]+[
        (b[0]+b[2]*float(s.get('entryX',0)), b[1]+b[3]*float(s.get('entryY',.5)))]


def along(points, ratio):
    lengths=[math.dist(a,b) for a,b in zip(points,points[1:])]
    goal=sum(lengths)*ratio
    for i,length in enumerate(lengths):
        if goal<=length or i==len(lengths)-1:
            a,b=points[i:i+2];t=goal/length if length else 0
            return a[0]+(b[0]-a[0])*t,a[1]+(b[1]-a[1])*t
        goal-=length
    return points[0]


def check_geometry(diagram):
    """Check unrelated boxes against saved connector routes, including diagonals."""
    cells={c.get('id'):c for c in diagram.findall('.//mxCell')};nodes={}
    def geom(c):
        ident=c.get('id')
        if ident in nodes:return nodes[ident]
        g=c.find('mxGeometry');x,y=float(g.get('x',0)),float(g.get('y',0))
        parent=cells.get(c.get('parent'))
        if parent is not None and parent.get('vertex')=='1':
            px,py,*_=geom(parent);x+=px;y+=py
        nodes[ident]=(x,y,float(g.get('width',0)),float(g.get('height',0)),c)
        return nodes[ident]
    for c in cells.values():
        if c.get('vertex')=='1' and c.get('kind')!='multiplicity':geom(c)
    def inside_segment(a,b,box):
        # Slab intersection with a slightly inset box excludes boundary touches.
        x,y,w,h,_=box;lo,hi=0.,1.
        for origin,delta,left,right in [(a[0],b[0]-a[0],x+3,x+w-3),(a[1],b[1]-a[1],y+3,y+h-3)]:
            if abs(delta)<1e-8:
                if not left<origin<right:return False
            else:
                u,v=(left-origin)/delta,(right-origin)/delta
                lo=max(lo,min(u,v));hi=min(hi,max(u,v))
                if lo>=hi:return False
        return lo<hi
    checked=0
    for edge in cells.values():
        if edge.get('edge')!='1':continue
        points=edge_points(edge,nodes);checked+=1
        excluded={edge.get('source'),edge.get('target')}
        for ident,box in nodes.items():
            if ident in excluded or box[4].get('kind') not in ('class','package','component','artifact','interface','rect','note'):continue
            assert not any(inside_segment(a,b,box) for a,b in zip(points,points[1:])), \
                f'Connector {diagram.get("id")}/{edge.get("id")} crosses unrelated {ident}'
    return checked


def render(diagram, output, scale=1.5):
    model=diagram.find('mxGraphModel'); w,h=int(model.get('pageWidth')),int(model.get('pageHeight'))
    im=Image.new('RGB',(round(w*scale),round(h*scale)),'white'); d=ImageDraw.Draw(im)
    cells={c.get('id'):c for c in diagram.findall('.//mxCell')}
    nodes={}
    def geom(c):
        id=c.get('id')
        if id in nodes:return nodes[id]
        g=c.find('mxGeometry');x,y=float(g.get('x',0)),float(g.get('y',0))
        if c.get('parent') not in ('0','1',None):
            parent=cells[c.get('parent')]
            if parent.get('vertex')=='1':
                px,py,*_=geom(parent);x+=px;y+=py
        nodes[id]=(x,y,float(g.get('width',0)),float(g.get('height',0)),c)
        return nodes[id]
    for c in cells.values():
        if c.get('vertex')=='1' and c.get('kind')!='multiplicity':geom(c)
    def pt(p):return tuple(round(v*scale) for v in p)
    def line(a,b,width=1.2):d.line([pt(a),pt(b)],fill='black',width=max(1,round(width*scale)))
    def rect(x,y,w,h,fill='white',outline='black'):d.rectangle([pt((x,y)),pt((x+w,y+h))],fill=fill,outline=outline,width=max(1,round(1.2*scale)))
    def polygon(points,fill='white'):d.polygon([pt(p) for p in points],fill=fill,outline='black',width=max(1,round(1.2*scale)))
    def text(value,x,y,width,height,size=22,bold=False,align='center',valign='middle',pad=10,background=False,wrap=True):
        font=ImageFont.truetype(BOLD if bold else FONT,round(size*scale));lines=[]
        for raw in plain(value).split('\n'):
            if wrap and d.textlength(raw,font=font)>(width-2*pad)*scale:
                acc=''
                for word in raw.split():
                    trial=(acc+' '+word).strip()
                    if acc and d.textlength(trial,font=font)>(width-2*pad)*scale:lines.append(acc);acc=word
                    else:acc=trial
                lines.append(acc)
            else:lines.append(raw)
        lh=size*1.22
        if len(lines)*lh>height+5 and height>0:
            raise ValueError(f'Text height overflow on {diagram.get("id")}: {value[:80]} ({len(lines)*lh} > {height})')
        yy=y+pad if valign=='top' else y+(height-len(lines)*lh)/2
        for i,t in enumerate(lines):
            tw=d.textlength(t,font=font)/scale
            if tw>width-2*pad+3 and width>0:
                raise ValueError(f'Text width overflow on {diagram.get("id")}: {t}')
            xx=x+pad if align=='left' else x+(width-tw)/2
            pos=(xx,yy+i*lh)
            if background:rect(pos[0]-4,pos[1],tw+8,lh,outline=None)
            d.text(pt(pos),t,font=font,fill='black')
    # Frames and containers are painted before edges, preserving nested deployment geometry.
    containers={'frame','boundary','node','runtime'}
    for x,y,ww,hh,c in nodes.values():
        kind=c.get('kind');s=styles(c)
        if kind not in containers:continue
        if kind=='node':
            z=16;polygon([(x,y+z),(x+z,y),(x+ww,y),(x+ww,y+hh-z),(x+ww-z,y+hh),(x,y+hh)])
            line((x,y+z),(x+ww-z,y+z));line((x+ww-z,y+z),(x+ww,y));line((x+ww-z,y+z),(x+ww-z,y+hh))
        else:rect(x,y,ww,hh,fill=None if kind in ('frame','boundary') else 'white')
        size=int(s.get('fontSize',22))
        text(c.get('value',''),x+10,y+15,ww-20,85 if kind=='node' else 64,size,s.get('fontStyle')=='1',valign='top',pad=0)
    labels=[]
    for c in cells.values():
        if c.get('edge')!='1':continue
        s=styles(c);points=edge_points(c,nodes)
        for a,b in zip(points,points[1:]):
            if s.get('dashed')=='1':
                dist=math.dist(a,b)
                for k in range(0,int(dist),12):
                    q=min(k+7,dist)
                    if dist:line((a[0]+(b[0]-a[0])*k/dist,a[1]+(b[1]-a[1])*k/dist),(a[0]+(b[0]-a[0])*q/dist,a[1]+(b[1]-a[1])*q/dist))
            else:line(a,b)
        for end,other,arrow,filled in [(points[-1],next((v for v in reversed(points[:-1]) if v!=points[-1]),points[0]),s.get('endArrow','none'),False),
                                       (points[0],next((v for v in points[1:] if v!=points[0]),points[-1]),s.get('startArrow','none'),s.get('startFill')=='1')]:
            if arrow=='none':continue
            dx,dy=end[0]-other[0],end[1]-other[1];norm=math.hypot(dx,dy)
            if not norm:continue
            dx/=norm;dy/=norm
            le=17 if arrow in ('block','diamond') else 13
            left=(end[0]-le*dx-7*dy,end[1]-le*dy+7*dx)
            right=(end[0]-le*dx+7*dy,end[1]-le*dy-7*dx)
            if arrow=='diamond':polygon([end,left,(end[0]-2*le*dx,end[1]-2*le*dy),right],fill='black' if filled else 'white')
            elif arrow=='block':polygon([end,left,right])
            else:line(left,end);line(end,right)
        if c.get('value'):
            x,y=along(points,.5);off=c.find('mxGeometry/mxPoint[@as="offset"]')
            if off is not None:x+=float(off.get('x',0));y+=float(off.get('y',0))
            labels.append((c.get('value'),x,y,int(s.get('fontSize',20))))
        for lab in [e for e in cells.values() if e.get('parent')==c.get('id') and e.get('kind')=='multiplicity']:
            gg=lab.find('mxGeometry');ratio=(float(gg.get('x'))+1)/2;x,y=along(points,ratio)
            off=gg.find('mxPoint');x+=float(off.get('x'));y+=float(off.get('y'))
            labels.append((lab.get('value'),x,y,20))
    for x,y,ww,hh,c in nodes.values():
        kind=c.get('kind');s=styles(c)
        if kind in containers:continue
        size=int(s.get('fontSize',22));bold=s.get('fontStyle')=='1'
        if kind=='class':
            rect(x,y,ww,hh);line((x,y+42),(x+ww,y+42));text(c.get('value',''),x,y,ww,42,size,bold)
            continue
        if kind=='uc':d.ellipse([pt((x,y)),pt((x+ww,y+hh))],fill='white',outline='black',width=max(1,round(1.4*scale)))
        elif kind=='actor':
            cx=x+ww/2;d.ellipse([pt((cx-12,y)),pt((cx+12,y+24))],fill='white',outline='black',width=2)
            for a,b in [((cx,y+24),(cx,y+58)),((cx-25,y+36),(cx+25,y+36)),((cx,y+58),(cx-22,y+83)),((cx,y+58),(cx+22,y+83))]:line(a,b)
            text(c.get('value',''),cx-115,y+hh+5,230,90,size,bold,pad=0)
            continue
        elif kind=='note':
            polygon([(x,y),(x+ww-18,y),(x+ww,y+18),(x+ww,y+hh),(x,y+hh)])
            line((x+ww-18,y),(x+ww-18,y+18));line((x+ww-18,y+18),(x+ww,y+18))
        elif kind=='package':
            rect(x,y+26,ww,hh-26);rect(x,y,150,26);line((x,y+26),(x+150,y+26))
        elif kind in ('component','artifact','interface','rect'):
            rect(x,y,ww,hh)
            if kind=='component':
                rect(x-9,y+hh*.3,22,15);rect(x-9,y+hh*.6,22,15)
        elif kind=='methods':line((x,y),(x+ww,y))
        align='left' if kind in ('attrs','methods','note') else 'center'
        valign='top' if kind in ('attrs','methods','note') else 'middle'
        text(c.get('value',''),x,y,ww,hh,size,bold,align,valign,pad=12,wrap=kind not in ('attrs','methods'))
    for value,x,y,size in labels:
        # Edge labels use measured dimensions and white backgrounds.
        font=ImageFont.truetype(FONT,round(size*scale));ls=plain(value).split('\n')
        tw=max(d.textlength(v,font=font) for v in ls)/scale
        text(value,x-tw/2-5,y-size*.61*len(ls),tw+10,size*1.22*len(ls),size,pad=0,background=True,wrap=False)
    output.parent.mkdir(exist_ok=True,parents=True);im.save(output)
    return im


def verify_and_render():
    report={'source_integrity':{},'files':{},'limitations':['PNG previews are rendered from saved mxGraph XML with the included renderer; they are not screenshots of diagrams.net.','UML classes and optional offline/AI capabilities describe the target design, not a claim of completed implementation.']}
    baseline=ROOT/'.work/source-hashes.json'
    if baseline.exists():
        for path,sha in json.loads(baseline.read_text(encoding='utf-8')).items():
            assert hashlib.sha256(Path(path).read_bytes()).hexdigest()==sha, 'Changed source: '+path
        report['source_integrity']={'checked':len(json.loads(baseline.read_text(encoding='utf-8'))),'unchanged':True}
    all_diagrams=[];thumbnails=[];diagram_ids=set()
    for filename,expected in [('lab4.drawio',6),('lab5.drawio',5),('lab6.drawio',2)]:
        root=ET.parse(ROOT/filename).getroot();dias=root.findall('diagram');assert len(dias)==expected
        stat={'pages':len(dias),'vertices':0,'edges':0,'routes_checked':0,'previews':[]}
        for dia in dias:
            assert dia.get('id') not in diagram_ids, 'Duplicate diagram ID'
            diagram_ids.add(dia.get('id'))
            ids=[c.get('id') for c in dia.findall('.//mxCell')];assert len(ids)==len(set(ids))
            for c in dia.findall('.//mxCell'):
                for attr in ['parent','source','target']:
                    if c.get(attr):assert c.get(attr) in ids, f'Broken {attr}: {c.attrib}'
                stat['vertices']+=int(c.get('vertex','0'));stat['edges']+=int(c.get('edge','0'))
            if filename!='lab4.drawio':stat['routes_checked']+=check_geometry(dia)
            out=PREVIEWS/(dia.get('id')+'.png')
            im=render(dia,out);stat['previews'].append(str(out.relative_to(ROOT)))
            im.thumbnail((700,550));thumbnails.append((dia.get('name'),im.copy()))
            all_diagrams.append(copy.deepcopy(dia))
        report['files'][filename]=stat
    combined=mxfile()
    for dia in all_diagrams:combined.append(dia)
    save(combined,ROOT/'Sensei-UML.drawio')
    use=ET.parse(ROOT/'lab4.drawio').getroot()
    uc={int(c.get('useCase')) for c in use.findall('.//mxCell') if c.get('useCase')}
    roles={c.get('role') for c in use.findall('.//mxCell') if c.get('role')}
    assert uc==set(range(1,37));assert roles=={'dev','author','admin'}
    fr=set()
    for _,_,_,refs in use_cases.UC.values():
        for match in re.finditer(r'FR(\d+)(?:[–-]FR(\d+))?',refs):
            fr.update(range(int(match[1]),int(match[2] or match[1])+1))
    assert fr==set(range(1,25))
    report['use_cases']={'unique':len(uc),'roles':sorted(roles),'includes':len(use.findall('.//mxCell[@relation="include"]')),'extends':len(use.findall('.//mxCell[@relation="extend"]'))}
    report['requirements_covered']=['FR'+str(n).zfill(2) for n in sorted(fr)]
    report['classes']={'unique':len({c['name'].replace(' «ref»','') for c in CATALOG}),'cards':len(CATALOG)}
    # Overview is a navigation aid; full-size PNGs retain readable text.
    sheet=Image.new('RGB',(1440,math.ceil(len(thumbnails)/2)*610),'#e7e7e7');d=ImageDraw.Draw(sheet)
    font=ImageFont.truetype(FONT,22)
    for i,(name,im) in enumerate(thumbnails):
        xx=(i%2)*720+10;yy=(i//2)*610+8
        d.text((xx+5,yy),name,font=font,fill='black');sheet.paste(im,(xx+5,yy+40))
    sheet.save(PREVIEWS/'overview.png')
    expected_previews={dia.get('id')+'.png' for dia in all_diagrams}|{'overview.png'}
    for stale in PREVIEWS.glob('*.png'):
        assert stale.resolve().is_relative_to((ROOT/'uml-previews').resolve())
        if stale.name not in expected_previews:stale.unlink()
    (ROOT/'uml-verification.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    return report


def write_readme(report):
    lines=['# UML-модели Sensei++ для ПИС, лабораторные работы 4-6','',
        'Редактируемые исходники: [все 13 листов](Sensei-UML.drawio), [ЛР4](lab4.drawio), [ЛР5](lab5.drawio), [ЛР6](lab6.drawio). Открывать в diagrams.net / draw.io; каждый блок, атрибут, метод, соединитель и подпись редактируется отдельно.','',
        'Модель продолжает предметную область существующих `lab2.drawio` и `lab3.drawio`. Это проектная модель целевой системы; она не ограничивается уже реализованными функциями. Дополнительные организационные роли не вводятся.','',
        '## Состав и соответствие заданиям','',
        '| Работа | Требование методички | Листы |','|---|---|---|',
        '| 4 | Актёры, иерархия, публичные прецеденты и отношения | 01-06: обзор, роли, обучение, работа/опыт, контент/администрирование, доступ/offline |',
        '| 5 | Пакеты и классы, атрибуты/методы, связи и кратности | 07-11: пакеты; контент/цели; практика/свидетельства; работа/опыт; доступ/sync |',
        '| 6 | Компоненты и технологии; устройства, среды, протоколы и размещение | 12-13: компоненты; развёртывание |','',
        '## Условные обозначения','',
        '- Use Case: актёр, овал прецедента, граница системы; include направлен к обязательному фрагменту, extend — от дополнения к базовому прецеденту. Пустой треугольник указывает на общую роль.',
        '- Классы: имя / атрибуты / операции. `−` — закрытый атрибут; `+` — открытая операция. Типы и сигнатуры описывают проект, а не буквальный API существующего C#-класса.',
        '- Сплошная линия классов — ассоциация; без стрелки навигация не ограничена. Чёрный ромб — композиция у целого. Пунктирная стрелка — зависимость от используемого элемента. Кратность у конца показывает число экземпляров этого конца.',
        '- `«ref»` помечает повторный показ того же проектного класса для читаемости; это локальное обозначение, не отдельный стандартный стереотип UML. Role — назначаемая роль, не подкласс пользователя.',
        '- Пакет — фигура с вкладкой. Компонент — прямоугольник со значком компонента. Узел развёртывания — объёмный блок; среда выполнения и поставляемые артефакты вложены в узел.','',
        '## Принятые решения','',
        'Единый сервер ASP.NET Core объединяет пять предметных модулей. Межмодульные стрелки — вызовы контрактов в одном процессе, а не микросервисы. ClientSync, автономные пакеты и ИИ отражают целевую систему. Базовая проверка объективных упражнений детерминированная и не зависит от внешнего ИИ.',
        'Автор и администратор наследуют личные функции разработчика. Это не открывает чужие данные. Подтверждение контекста, одобрение точной редакции опыта и просмотр точной версии рассказа разделены. Новая редакция фактов требует нового одобрения.',
        'В диаграмме развёртывания выбран допустимый вариант: ASP.NET Core и PostgreSQL на одном сервере, клиент обращается по HTTPS. Отдельный reverse proxy не требуется моделью. Указаны предварительные ресурсы из требований: 4 ядра, 16 ГБ RAM, 20 ГБ свободного места; это не измеренные характеристики компьютера. .NET 10 подтверждён Sensei.Host.csproj, PostgreSQL 18 — compose.yaml.','',
        '## Каталог прецедентов и трассировка','',
        '| Код | Прецедент | Роль | Требования |','|---|---|---|---|']
    for n,(name,role,desc,refs) in use_cases.UC.items():
        lines.append(f'| UC{n:02} | {name.replace(chr(10)," ")} | {role} | {refs} |')
    lines+=['','### Обязательные включения','', '| Базовый | Включаемый |','|---|---|']
    for a,b in use_cases.INCLUDES:lines.append(f'| UC{a:02} | UC{b:02} |')
    lines+=['','### Условные расширения','', '| Расширение → база | Условие | Точка расширения |','|---|---|---|']
    for a,b,cond,point in use_cases.EXTENDS:lines.append(f'| UC{a:02} → UC{b:02} | {cond} | {point} |')
    lines+=['','## Каталог проектных классов','',
        'Имена классов и типов оставлены английскими, пояснения — русские. `Guid` — идентификатор; `Instant` — момент времени; `Json` — структурированный полезный груз. Подробные атрибуты и операции находятся в редактируемых отсеках классов и в `uml-class-catalog.json`.','',
        '| Лист | Класс | Назначение операций |','|---|---|---|']
    for c in CATALOG:
        if not c['external']:lines.append(f'| {c["page"]} | {c["name"]} | {"; ".join(c["operations"])} |')
    lines+=['','## Связи, требующие уточнения при реализации','',
        '- ConceptRelation хранит две ссылки на Concept (source и target). На листе 08 одна линия сворачивает две однотипные роли; для каждой ссылки кратность Concept равна 1.',
        '- Все личные агрегаты имеют владельца. Чтобы не повторять одинаковые линии на каждом листе, типовая связь UserAccount (1) → личный объект (0..*) показана на LearningSession и ClientDevice; остальные OwnerId явно указаны в классах и пояснениях.',
        '- ExerciseVersion имеет одну основную тему; дополнительные темы реализуются отдельными ссылками при детализации. Исторические версии не удаляются вместе с назначениями.',
        '- Role показывает назначаемые системные роли; Developer — базовая роль каждого зарегистрированного пользователя. Загрузка пакета и синхронизация требуют сети; выполнение по активированному пакету — нет.',
        '- ProfileProjection — пересчитываемое представление, не первичный источник знания. EvidenceObservation может иметь источником результат, одобренный опыт или выбранный импорт.',
        '- ReviewAcknowledgement подтверждает точную пару версий контекста и обсуждения. Он не является ExperienceApproval и не разрешает экспорт PresentationRevision.',
        '- В компонентной схеме показаны основные порты. Авторизация и Persistence используются всеми защищёнными модулями; повторяющиеся линии сведены в пояснение.',
        '- RPO 24 часа и RTO 2 часа — целевые требования; их нельзя считать выполненными до испытания восстановления. SMB 3 — выбранный вариант передачи копий на сетевое хранилище; отдельный диск использует файловый ввод-вывод.','',
        '## Исходные материалы','',
        '- `terms/_ПИС_Лаб_4.pdf`, `terms/_ПИС_Лаб_5.pdf`, `terms/_ПИС_Лаб_6.pdf` — состав необходимых диаграмм.',
        '- `lab2.drawio`, `lab3.drawio` — предметная область и согласованность с функциональными моделями.',
        '- `D:/BSTU/Projects/TRWP_7SEM/Sensei++/docs/12-domain-analysis-and-requirements.md` — сущности, роли и технические требования.',
        '- `D:/BSTU/Projects/TRWP_7SEM/Sensei++/docs/14-functional-model-idef0.md` — FR01-FR24.',
        '- `D:/BSTU/Projects/TRWP_7SEM/Sensei++/docs/models/Sensei-UseCase.drawio` и `generate_use_case.py` — повторно использованная и проверенная модель прецедентов.',
        '- `D:/BSTU/Projects/NODEJS_6SEM/Docs/MediaGiant-UseCase.drawio`, `Диаграмма_архитектуры_приложения.drawio`, `deployment_diagram.drawio`, `Диаграммы.docx` — оформление: чёрно-белая UML, Times New Roman, актёры/овалы, отсеки, узлы и вложенные компоненты. ER-стрелки и предметная область примеров в UML классов не перенесены.','',
        '## Проверка и воспроизведение','',
        'Запуск из PIS: `python scripts/build_uml.py` (Python 3 + Pillow; шрифты Times New Roman из Windows). Генератор сохраняет XML, перечитывает файлы, проверяет ссылки, число листов, три роли и 36 прецедентов, затем рисует PNG по сохранённым координатам. Все PNG в `uml-previews/`; `overview.png` — обзор, не замена полноразмерных листов.',
        'Превью получены встроенным в генератор рендерером XML, а не снимками diagrams.net. Переносы текста проверены рендерером. Отдельные особенности отрисовки редактора могут отличаться. Файл `uml-verification.json` содержит результаты; исходные файлы не изменялись.','',
        '## Превью по листам','']
    for filename in ['lab4.drawio','lab5.drawio','lab6.drawio']:
        for dia in ET.parse(ROOT/filename).getroot().findall('diagram'):
            lines.append(f'- [{dia.get("name")}](uml-previews/{dia.get("id")}.png)')
    (ROOT/'UML-README.md').write_text('\n'.join(lines)+'\n',encoding='utf-8')
    (ROOT/'uml-class-catalog.json').write_text(json.dumps(CATALOG,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')


if __name__=='__main__':
    use_cases.build_model()
    uc_root=ET.parse(ROOT/'lab4.drawio').getroot()
    for diagram in uc_root.findall('diagram'):
        diagram.set('id','uc-'+diagram.get('id'))
    save(uc_root,ROOT/'lab4.drawio')
    build_structural()
    build_physical()
    result=verify_and_render()
    write_readme(result)
    print(json.dumps({k:v for k,v in result.items() if k!='files'},ensure_ascii=False,indent=2))
    print('Created: lab4.drawio (6), lab5.drawio (5), lab6.drawio (2), Sensei-UML.drawio (13)')
