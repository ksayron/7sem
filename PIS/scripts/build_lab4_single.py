"""Single Lab 4 diagram with element styles copied from MediaGiant-UseCase.

The six-page model and all reference files are read-only inputs.
"""
from pathlib import Path
import copy
import hashlib
import json
import xml.etree.ElementTree as ET
import use_cases

ROOT=Path(__file__).resolve().parents[1]
REFERENCE=Path(r'D:\BSTU\Projects\NODEJS_6SEM\Docs\MediaGiant-UseCase.drawio')
OUTPUT=ROOT/'lab4-single.drawio'


def sha(path):return hashlib.sha256(path.read_bytes()).hexdigest()


def main():
    protected=[REFERENCE,ROOT/'lab4.drawio',ROOT/'lab5.drawio',ROOT/'lab6.drawio',ROOT/'Sensei-UML.drawio']
    baseline={str(p):sha(p) for p in protected}
    template=ET.parse(REFERENCE).getroot()
    originals=template.findall('.//mxCell')
    boundary=next(c for c in originals if c.get('vertex')=='1' and not c.get('value'))
    ellipse=next(c for c in originals if c.get('style','').startswith('ellipse;'))
    actor=next(c for c in originals if 'shape=umlActor;' in c.get('style','') and c.get('value','').startswith('Пользователь'))
    association=next(c for c in originals if c.get('edge')=='1' and c.get('style','').startswith('endArrow=none;html=1;strokeWidth=1;curved=1;exitX=1;'))
    dependency=next(c for c in originals if c.get('edge')=='1' and c.get('value')=='&lt;&lt;extend&gt;&gt;')
    generalization=next(c for c in originals if c.get('edge')=='1' and 'endArrow=block;endFill=0;' in c.get('style',''))
    root=ET.Element('mxfile',**template.attrib)
    root.set('pages','1');root.set('compressed','false')
    diagram=ET.SubElement(root,'diagram',id='sensei-lab4-single',name='Страница 1')
    model=ET.SubElement(diagram,'mxGraphModel',**template.find('.//mxGraphModel').attrib)
    cells=ET.SubElement(model,'root')
    ET.SubElement(cells,'mxCell',id='0');ET.SubElement(cells,'mxCell',id='1',parent='0')
    nodes={}
    def node(id,label,x,y,w,h,source,**metadata):
        c=ET.SubElement(cells,'mxCell',id=id,parent='1',style=source.get('style'),value=label,vertex='1',**metadata)
        ET.SubElement(c,'mxGeometry',x=str(x),y=str(y),width=str(w),height=str(h),**{'as':'geometry'})
        nodes[id]=(x,y,w,h)
        return c
    bgeom=boundary.find('mxGeometry')
    node('system','',*[float(bgeom.get(k)) for k in ('x','y','width','height')],boundary)
    for role,label,x,y in [('admin','Администратор<br>системы',360,120),
                            ('dev','Разработчик<br>(пользователь)',350,1065),
                            ('author','Автор учебных<br>материалов',350,1670)]:
        role_source=next(c for c in originals if c.get('vertex')=='1' and 'shape=umlActor;' in c.get('style','')
                         and c.get('value','').startswith('Администратор' if role=='admin' else 'Гость' if role=='author' else 'Пользователь'))
        node(role,label,x,y,30,60,role_source,role=role)

    # Compact ovals and a tall fan layout, using the same dimensions as the reference.
    primary_y={1:30,2:115,3:200,4:285,5:370,6:455,7:540,11:700,12:785,
               13:870,14:955,15:1040,18:1150,20:1235,21:1320,22:1405,
               29:1500,30:1585,31:1670}
    positions={n:(540,y,140,70) for n,y in primary_y.items()}
    positions.update({
        27:(730,-130,140,70),28:(1070,-130,173.46,90),
        9:(810,455,120,60),10:(810,620,140,70),8:(1070,540,140,70),
        16:(830,700,140,70),36:(830,870,140,70),17:(830,970,140,70),
        34:(1110,850,173.46,90),19:(830,1150,140,70),
        35:(830,1405,173.46,90),32:(830,1670,140,70),
        23:(540,1780,140,70),24:(780,1760,140,70),25:(1030,1780,140,70),
        26:(780,1855,140,70),33:(1110,1860,173.46,65),
    })
    assert set(positions)==set(range(1,37))
    labels={
        3:'Управлять профилем<br>и приватностью',4:'Выбрать цели и<br>маршрут обучения',
        5:'Изучить тему и<br>материалы',6:'Пройти учебную<br>сессию',
        8:'Сохранить попытку,<br>результат и помощь',
        10:'Запросить объяснение<br>ИИ по ответу',
        11:'Просмотр истории,<br>свидетельств и профиля',
        12:'Повторить тему<br>по рекомендации',
        13:'Вести рабочие<br>и учебные эпизоды',
        14:'Обсудить контекст<br>и инженерные<br>решения',
        15:'Подтвердить<br>рассмотренную версию<br>контекста',
        16:'Оспорить, исправить<br>или отозвать<br>наблюдение',
        17:'Запросить<br>интерпретацию<br>контекста у ИИ',
        18:'Создать и<br>редактировать запись<br>опыта',
        19:'Запросить черновик<br>опыта у ИИ',
        20:'Подтвердить точную<br>редакцию опыта',
        21:'Подготовить и<br>репетировать рассказ',
        22:'Экспортировать<br>проверенный рассказ',
        23:'Вести каталог<br>понятий и их связей',
        24:'Подготовить версии<br>материалов и<br>упражнений',
        25:'Проверить и<br>опубликовать контент',
        26:'Вывести дефектный<br>контент из<br>использования',
        27:'Управлять учётными<br>записями и ролями',
        28:'Контроль системы,<br>резервное копирование<br>и восстановление',
        29:'Загрузить учебный<br>пакет',30:'Выполнить<br>упражнение без сети',
        31:'Синхронизировать<br>личные данные',
        32:'Разрешить конфликт<br>версий',
        33:'Проверить ответы,<br>версии и связи<br>контента',
        34:'Просмотреть и разрешить<br>передачу контекста',
        35:'Проверить одобрение<br>редакции опыта<br>и версии рассказа',
        36:'Импортировать<br>выбранный контекст<br>из интеграции',
    }
    for n,(name,role,desc,fr) in use_cases.UC.items():
        node('uc'+str(n),labels.get(n,name.replace('\n',' ')),*positions[n],ellipse,
             useCase=str(n),requirements=fr,description=desc)

    counter=0
    def edge(a,b,source,label='',sides=(1,.5,0,.5),points=None,relation='association',offset=None,condition=None,extension_point=None):
        nonlocal counter
        counter+=1
        sx,sy,tx,ty=sides
        # Geometry anchors change for Sensei++; all visual settings come from the reference.
        st=[p for p in source.get('style').split(';') if p and not p.startswith(('exitX=','exitY=','entryX=','entryY=','exitDx=','exitDy=','entryDx=','entryDy=','exitPerimeter=','entryPerimeter='))]
        st.extend([f'exitX={sx}',f'exitY={sy}','exitDx=0','exitDy=0',f'entryX={tx}',f'entryY={ty}','entryDx=0','entryDy=0','exitPerimeter=0','entryPerimeter=0'])
        c=ET.SubElement(cells,'mxCell',id='edge'+str(counter),parent='1',source=a,target=b,
                        style=';'.join(st)+';',value=label,edge='1',relation=relation)
        if condition:c.set('condition',condition)
        if extension_point:c.set('extensionPoint',extension_point)
        g=ET.SubElement(c,'mxGeometry',relative='1',**{'as':'geometry'})
        if points:
            arr=ET.SubElement(g,'Array',**{'as':'points'})
            for x,y in points:ET.SubElement(arr,'mxPoint',x=str(x),y=str(y))
        if offset:ET.SubElement(g,'mxPoint',x=str(offset[0]),y=str(offset[1]),**{'as':'offset'})
        return c
    # Direct actor fans are intentionally retained from the reference, without buses.
    for n in primary_y:
        edge('dev','uc'+str(n),association,sides=(1,1/3,0,.5))
    edge('admin','uc27',association,sides=(1,1/3,0,.5))
    edge('admin','uc28',association,sides=(1,1/3,.5,1),points=[(445,-25),(1156.73,-25)])
    for n in [23,24,25,26]:
        edge('author','uc'+str(n),association,sides=(1,1/3,.5,0) if n==25 else (1,1/3,0,.5),
             points=[(475,1750),(1100,1750)] if n==25 else [(475,1750),(740,1750)] if n==24 else [(450,1890)] if n==26 else None)
    # Generalization is outside the boundary, keeping the three actors visible together.
    edge('admin','dev',generalization,sides=(0,.5,0,.5),points=[(260,150),(260,1095)],relation='generalization')
    edge('author','dev',generalization,sides=(0,.5,0,.5),points=[(295,1700),(295,1095)],relation='generalization')
    for a,b in use_cases.INCLUDES:
        points=None;offset=None;sides=(1,.5,0,.5)
        if (a,b)==(30,8):
            sides=(1,.5,1,.5);points=[(1305,1620),(1305,575)];offset=(45,0)
        elif b==34:
            lane={10:1030,17:1055,19:1080}[a]
            sa=positions[a];ta=positions[b]
            points=[(lane,sa[1]+sa[3]/2),(lane,ta[1]+ta[3]/2)]
            offset=(-40,0) if a==10 else (0,15) if a==17 else (45,0)
        elif (a,b)==(25,33):sides=(.5,1,.5,0);offset=(60,0)
        edge('uc'+str(a),'uc'+str(b),dependency,'&lt;&lt;include&gt;&gt;',sides,points,'include',offset)
    for a,b,condition,point in use_cases.EXTENDS:
        edge('uc'+str(a),'uc'+str(b),dependency,'&lt;&lt;extend&gt;&gt;',
             sides=(0,.5,1,.5),relation='extend',offset=(0,-10),condition=condition,extension_point=point)
    ET.indent(root,space='  ')
    OUTPUT.write_bytes(ET.tostring(root,encoding='utf-8',xml_declaration=True))
    saved=ET.parse(OUTPUT).getroot();ids={c.get('id') for c in saved.findall('.//mxCell')}
    assert len(saved.findall('diagram'))==1
    assert len(ids)==len(saved.findall('.//mxCell'))
    for c in saved.findall('.//mxCell'):
        for key in ['parent','source','target']:
            if c.get(key):assert c.get(key) in ids
    ovals=saved.findall('.//mxCell[@useCase]')
    assert len(ovals)==36 and all(c.get('style')==ellipse.get('style') for c in ovals)
    assert all(sha(Path(p))==h for p,h in baseline.items())
    report={'file':str(OUTPUT),'pages':1,'use_cases':36,'actors':3,'includes':7,'extends':7,
            'generalizations':2,'oval_styles_copied_verbatim':True,'actor_styles_copied_verbatim':True,
            'boundary_style_and_geometry_copied_verbatim':True,'reference':str(REFERENCE),
            'protected_input_hashes':baseline,'inputs_unchanged':True}
    (ROOT/'lab4-single-verification.json').write_text(json.dumps(report,ensure_ascii=False,indent=2)+'\n',encoding='utf-8')
    print(json.dumps(report,ensure_ascii=False,indent=2))


if __name__=='__main__':main()
