from __future__ import annotations

from pathlib import Path

from docx import Document
from docx.enum.section import WD_SECTION
from docx.enum.style import WD_STYLE_TYPE
from docx.enum.table import WD_CELL_VERTICAL_ALIGNMENT, WD_TABLE_ALIGNMENT
from docx.enum.text import WD_ALIGN_PARAGRAPH, WD_BREAK, WD_LINE_SPACING
from docx.oxml import OxmlElement
from docx.oxml.ns import qn
from docx.shared import Cm, Inches, Pt, RGBColor


ROOT = Path(__file__).resolve().parents[1]
OUTPUT = ROOT / "deliverables" / "Устав проекта Burmalda Factory.docx"

BLACK = "000000"
NAVY = "243447"
LIGHT_BLUE = "EAF0F5"
LIGHT_GRAY = "F3F4F6"
BORDER = "D9D9D9"
MUTED = "666666"


def set_cell_shading(cell, fill: str) -> None:
    tc_pr = cell._tc.get_or_add_tcPr()
    shd = tc_pr.find(qn("w:shd"))
    if shd is None:
        shd = OxmlElement("w:shd")
        tc_pr.append(shd)
    shd.set(qn("w:fill"), fill)


def set_cell_margins(cell, top=95, start=120, bottom=95, end=120) -> None:
    tc = cell._tc
    tc_pr = tc.get_or_add_tcPr()
    tc_mar = tc_pr.first_child_found_in("w:tcMar")
    if tc_mar is None:
        tc_mar = OxmlElement("w:tcMar")
        tc_pr.append(tc_mar)
    for tag, value in (("top", top), ("start", start), ("bottom", bottom), ("end", end)):
        node = tc_mar.find(qn(f"w:{tag}"))
        if node is None:
            node = OxmlElement(f"w:{tag}")
            tc_mar.append(node)
        node.set(qn("w:w"), str(value))
        node.set(qn("w:type"), "dxa")


def set_table_borders(table, color=BORDER, size="6") -> None:
    tbl_pr = table._tbl.tblPr
    borders = tbl_pr.find(qn("w:tblBorders"))
    if borders is None:
        borders = OxmlElement("w:tblBorders")
        tbl_pr.append(borders)
    for edge in ("top", "left", "bottom", "right", "insideH", "insideV"):
        tag = borders.find(qn(f"w:{edge}"))
        if tag is None:
            tag = OxmlElement(f"w:{edge}")
            borders.append(tag)
        tag.set(qn("w:val"), "single")
        tag.set(qn("w:sz"), size)
        tag.set(qn("w:space"), "0")
        tag.set(qn("w:color"), color)


def set_repeat_table_header(row) -> None:
    tr_pr = row._tr.get_or_add_trPr()
    tbl_header = OxmlElement("w:tblHeader")
    tbl_header.set(qn("w:val"), "true")
    tr_pr.append(tbl_header)


def set_row_cant_split(row) -> None:
    tr_pr = row._tr.get_or_add_trPr()
    cant_split = tr_pr.find(qn("w:cantSplit"))
    if cant_split is None:
        cant_split = OxmlElement("w:cantSplit")
        tr_pr.append(cant_split)
    cant_split.set(qn("w:val"), "true")


def set_cell_width(cell, width_cm: float) -> None:
    tc_pr = cell._tc.get_or_add_tcPr()
    tc_w = tc_pr.find(qn("w:tcW"))
    if tc_w is None:
        tc_w = OxmlElement("w:tcW")
        tc_pr.append(tc_w)
    twips = int(width_cm / 2.54 * 1440)
    tc_w.set(qn("w:w"), str(twips))
    tc_w.set(qn("w:type"), "dxa")


def set_font(run, name="Arial", size=11, bold=None, color=BLACK, italic=None) -> None:
    run.font.name = name
    run._element.get_or_add_rPr().get_or_add_rFonts().set(qn("w:ascii"), name)
    run._element.get_or_add_rPr().get_or_add_rFonts().set(qn("w:hAnsi"), name)
    run._element.get_or_add_rPr().get_or_add_rFonts().set(qn("w:eastAsia"), name)
    run.font.size = Pt(size)
    run.font.color.rgb = RGBColor.from_string(color)
    if bold is not None:
        run.bold = bold
    if italic is not None:
        run.italic = italic


def clear_paragraph(paragraph) -> None:
    for run in list(paragraph.runs):
        paragraph._p.remove(run._r)


def add_field(paragraph, instruction: str, display="1") -> None:
    run = paragraph.add_run()
    fld_char = OxmlElement("w:fldChar")
    fld_char.set(qn("w:fldCharType"), "begin")
    instr = OxmlElement("w:instrText")
    instr.set(qn("xml:space"), "preserve")
    instr.text = instruction
    separate = OxmlElement("w:fldChar")
    separate.set(qn("w:fldCharType"), "separate")
    text = OxmlElement("w:t")
    text.text = display
    end = OxmlElement("w:fldChar")
    end.set(qn("w:fldCharType"), "end")
    run._r.extend([fld_char, instr, separate, text, end])
    set_font(run, size=9, color=MUTED)


def add_toc(paragraph) -> None:
    run = paragraph.add_run()
    begin = OxmlElement("w:fldChar")
    begin.set(qn("w:fldCharType"), "begin")
    instr = OxmlElement("w:instrText")
    instr.set(qn("xml:space"), "preserve")
    instr.text = 'TOC \\o "1-1" \\h \\z \\u'
    separate = OxmlElement("w:fldChar")
    separate.set(qn("w:fldCharType"), "separate")
    placeholder = OxmlElement("w:t")
    placeholder.text = "Оглавление будет обновлено при открытии документа"
    end = OxmlElement("w:fldChar")
    end.set(qn("w:fldCharType"), "end")
    run._r.extend([begin, instr, separate, placeholder, end])
    set_font(run, size=10, color=MUTED)


def set_update_fields_on_open(doc: Document) -> None:
    settings = doc.settings._element
    update = settings.find(qn("w:updateFields"))
    if update is None:
        update = OxmlElement("w:updateFields")
        settings.append(update)
    update.set(qn("w:val"), "true")


def style_document(doc: Document) -> None:
    styles = doc.styles
    normal = styles["Normal"]
    normal.font.name = "Arial"
    normal._element.rPr.rFonts.set(qn("w:ascii"), "Arial")
    normal._element.rPr.rFonts.set(qn("w:hAnsi"), "Arial")
    normal.font.size = Pt(11)
    normal.font.color.rgb = RGBColor.from_string(BLACK)
    normal.paragraph_format.space_after = Pt(6)
    normal.paragraph_format.line_spacing = 1.12
    normal.paragraph_format.widow_control = True

    title = styles["Title"]
    title.font.name = "Arial"
    title._element.rPr.rFonts.set(qn("w:ascii"), "Arial")
    title._element.rPr.rFonts.set(qn("w:hAnsi"), "Arial")
    title.font.size = Pt(28)
    title.font.bold = True
    title.font.color.rgb = RGBColor.from_string(BLACK)
    title.paragraph_format.space_after = Pt(16)
    title.paragraph_format.keep_with_next = True
    title_ppr = title._element.get_or_add_pPr()
    title_border = title_ppr.find(qn("w:pBdr"))
    if title_border is not None:
        title_ppr.remove(title_border)

    for name, size, before, after in (
        ("Heading 1", 16, 18, 8),
        ("Heading 2", 13, 13, 6),
        ("Heading 3", 11, 9, 4),
    ):
        style = styles[name]
        style.font.name = "Arial"
        style._element.rPr.rFonts.set(qn("w:ascii"), "Arial")
        style._element.rPr.rFonts.set(qn("w:hAnsi"), "Arial")
        style.font.size = Pt(size)
        style.font.bold = True
        style.font.color.rgb = RGBColor.from_string(BLACK)
        style.paragraph_format.space_before = Pt(before)
        style.paragraph_format.space_after = Pt(after)
        style.paragraph_format.keep_with_next = True
        style.paragraph_format.keep_together = True

    if "Document Subtitle" not in styles:
        subtitle = styles.add_style("Document Subtitle", WD_STYLE_TYPE.PARAGRAPH)
    else:
        subtitle = styles["Document Subtitle"]
    subtitle.font.name = "Arial"
    subtitle._element.rPr.rFonts.set(qn("w:ascii"), "Arial")
    subtitle._element.rPr.rFonts.set(qn("w:hAnsi"), "Arial")
    subtitle.font.size = Pt(15)
    subtitle.font.color.rgb = RGBColor.from_string(BLACK)
    subtitle.paragraph_format.space_after = Pt(18)

    if "Small Text" not in styles:
        small = styles.add_style("Small Text", WD_STYLE_TYPE.PARAGRAPH)
    else:
        small = styles["Small Text"]
    small.font.name = "Arial"
    small._element.rPr.rFonts.set(qn("w:ascii"), "Arial")
    small._element.rPr.rFonts.set(qn("w:hAnsi"), "Arial")
    small.font.size = Pt(9)
    small.font.color.rgb = RGBColor.from_string(MUTED)
    small.paragraph_format.space_after = Pt(3)


def configure_section(section, first=False) -> None:
    section.page_width = Cm(21)
    section.page_height = Cm(29.7)
    section.top_margin = Cm(2.0)
    section.bottom_margin = Cm(1.8)
    section.left_margin = Cm(2.1)
    section.right_margin = Cm(2.1)
    section.header_distance = Cm(0.8)
    section.footer_distance = Cm(0.8)
    if first:
        section.different_first_page_header_footer = True


def add_header_footer(section) -> None:
    header = section.header
    p = header.paragraphs[0]
    clear_paragraph(p)
    p.alignment = WD_ALIGN_PARAGRAPH.RIGHT
    p.paragraph_format.space_after = Pt(0)
    run = p.add_run("BURMALDA FACTORY   |   УСТАВ ПРОЕКТА")
    set_font(run, size=8.5, color=MUTED)

    footer = section.footer
    p = footer.paragraphs[0]
    clear_paragraph(p)
    p.alignment = WD_ALIGN_PARAGRAPH.CENTER
    p.paragraph_format.space_after = Pt(0)
    run = p.add_run("Версия 1.0   |   Страница ")
    set_font(run, size=9, color=MUTED)
    add_field(p, "PAGE", "1")
    run = p.add_run(" из ")
    set_font(run, size=9, color=MUTED)
    add_field(p, "NUMPAGES", "1")


def add_paragraph(doc, text="", *, style=None, bold_lead=None, keep=False):
    p = doc.add_paragraph(style=style)
    if bold_lead and text.startswith(bold_lead):
        r1 = p.add_run(bold_lead)
        set_font(r1, bold=True)
        r2 = p.add_run(text[len(bold_lead):])
        set_font(r2)
    else:
        run = p.add_run(text)
        set_font(run)
    if keep:
        p.paragraph_format.keep_with_next = True
    return p


def add_bullets(doc, items, level=0):
    for item in items:
        p = doc.add_paragraph(style="List Bullet" if level == 0 else "List Bullet 2")
        p.paragraph_format.space_after = Pt(3)
        for run in p.runs:
            set_font(run)
        if not p.runs:
            set_font(p.add_run(item))
        else:
            p.runs[0].text = item


def add_numbered(doc, items):
    for index, item in enumerate(items, start=1):
        p = doc.add_paragraph()
        p.paragraph_format.left_indent = Cm(0.9)
        p.paragraph_format.first_line_indent = Cm(-0.55)
        p.paragraph_format.space_after = Pt(3)
        set_font(p.add_run(f"{index}. "))
        set_font(p.add_run(item))


def add_table(doc, headers, rows, widths=None, font_size=9.5, alignments=None):
    table = doc.add_table(rows=1, cols=len(headers))
    table.alignment = WD_TABLE_ALIGNMENT.CENTER
    table.autofit = False
    set_table_borders(table)
    hdr = table.rows[0]
    set_repeat_table_header(hdr)
    set_row_cant_split(hdr)
    for i, text in enumerate(headers):
        cell = hdr.cells[i]
        set_cell_shading(cell, NAVY)
        set_cell_margins(cell)
        cell.vertical_alignment = WD_CELL_VERTICAL_ALIGNMENT.CENTER
        if widths:
            set_cell_width(cell, widths[i])
        p = cell.paragraphs[0]
        p.alignment = WD_ALIGN_PARAGRAPH.CENTER
        p.paragraph_format.space_after = Pt(0)
        r = p.add_run(text)
        set_font(r, size=font_size, bold=True, color="FFFFFF")

    for row_index, row_values in enumerate(rows):
        new_row = table.add_row()
        set_row_cant_split(new_row)
        cells = new_row.cells
        for i, value in enumerate(row_values):
            cell = cells[i]
            set_cell_margins(cell)
            cell.vertical_alignment = WD_CELL_VERTICAL_ALIGNMENT.CENTER
            if widths:
                set_cell_width(cell, widths[i])
            if row_index % 2 == 1:
                set_cell_shading(cell, LIGHT_BLUE)
            p = cell.paragraphs[0]
            p.alignment = (alignments[i] if alignments else WD_ALIGN_PARAGRAPH.LEFT)
            p.paragraph_format.space_after = Pt(0)
            p.paragraph_format.line_spacing = 1.05
            r = p.add_run(str(value))
            set_font(r, size=font_size)
    doc.add_paragraph().paragraph_format.space_after = Pt(1)
    return table


def add_heading(doc, text, level=1):
    return doc.add_heading(text, level=level)


def add_cover(doc):
    p = doc.add_paragraph()
    p.paragraph_format.space_after = Pt(50)
    p.alignment = WD_ALIGN_PARAGRAPH.RIGHT
    r = p.add_run("BEBRA BEAVERS")
    set_font(r, size=10, bold=True, color=MUTED)

    p = doc.add_paragraph(style="Title")
    p.alignment = WD_ALIGN_PARAGRAPH.LEFT
    p.paragraph_format.space_before = Pt(38)
    p.add_run("Устав проекта\nBurmalda Factory")
    for run in p.runs:
        set_font(run, size=28, bold=True)

    p = doc.add_paragraph(style="Document Subtitle")
    p.alignment = WD_ALIGN_PARAGRAPH.LEFT
    r = p.add_run("Разработка и выпуск компьютерной игры об автоматизации производства бурмалды")
    set_font(r, size=15)

    p = doc.add_paragraph()
    p.paragraph_format.space_before = Pt(35)
    p.paragraph_format.space_after = Pt(0)
    r = p.add_run("Версия 1.0")
    set_font(r, size=11, bold=True)
    p = doc.add_paragraph()
    p.paragraph_format.space_after = Pt(0)
    r = p.add_run("Дата утверждения 17 сентября 2025 года")
    set_font(r, size=11)
    p = doc.add_paragraph()
    r = p.add_run("Плановый выпуск 19 ноября 2026 года")
    set_font(r, size=11)

    for _ in range(6):
        doc.add_paragraph()
    p = doc.add_paragraph()
    p.alignment = WD_ALIGN_PARAGRAPH.LEFT
    r = p.add_run("Минск 2025")
    set_font(r, size=10, color=MUTED)
    doc.add_page_break()


def build_document() -> Document:
    doc = Document()
    style_document(doc)
    configure_section(doc.sections[0], first=True)
    add_header_footer(doc.sections[0])
    set_update_fields_on_open(doc)

    props = doc.core_properties
    props.title = "Устав проекта Burmalda Factory"
    props.subject = "Разработка и выпуск игры об автоматизации производства бурмалды"
    props.author = "Проектная команда Burmalda Factory"
    props.comments = "Утверждённая базовая редакция устава проекта"

    add_cover(doc)

    add_heading(doc, "Паспорт документа", 1)
    add_paragraph(
        doc,
        "Устав фиксирует назначение, границы, критерии приёмки и правила управления проектом Burmalda Factory. Документ даёт руководителю проекта право организовывать работы в пределах утверждённых сроков, бюджета и содержания, а заказчику — единые основания для контроля и приёмки результата.",
    )
    add_table(
        doc,
        ["Параметр", "Значение"],
        [
            ["Полное наименование", "Разработка и выпуск компьютерной игры Burmalda Factory"],
            ["Краткое наименование", "Burmalda Factory"],
            ["Заказчик и спонсор", "Кабан Кабаныч"],
            ["Координатор заказчика", "Андрей Манагерович"],
            ["Руководитель проекта", "Кучерук Николай"],
            ["Исполнитель", "Игровая студия Bebra Beavers"],
            ["Срок проекта", "17 сентября 2025 года — 19 ноября 2026 года"],
            ["Формат работы", "Распределённая команда, удалённая разработка"],
            ["Целевая платформа", "Персональные компьютеры под управлением Windows 10 и Windows 11"],
            ["Канал выпуска", "Steam"],
            ["Базовый бюджет", "420 000 BYN, включая управленческий резерв 36 000 BYN"],
            ["Статус документа", "Утверждённая базовая редакция"],
        ],
        widths=[4.8, 11.4],
        font_size=9.5,
    )

    add_heading(doc, "Согласование", 2)
    add_table(
        doc,
        ["Роль", "ФИО", "Решение", "Дата"],
        [
            ["Заказчик и спонсор", "Кабан Кабаныч", "Утверждено", "17.09.2025"],
            ["Координатор заказчика", "Андрей Манагерович", "Согласовано", "17.09.2025"],
            ["Руководитель проекта", "Кучерук Николай", "Принято к исполнению", "17.09.2025"],
        ],
        widths=[4.5, 4.2, 4.8, 2.7],
        font_size=9,
        alignments=[WD_ALIGN_PARAGRAPH.LEFT, WD_ALIGN_PARAGRAPH.LEFT, WD_ALIGN_PARAGRAPH.CENTER, WD_ALIGN_PARAGRAPH.CENTER],
    )
    doc.add_page_break()

    add_heading(doc, "Оглавление", 1)
    toc_p = doc.add_paragraph()
    toc_p.paragraph_format.space_after = Pt(0)
    add_toc(toc_p)
    doc.add_page_break()

    add_heading(doc, "1 Общие положения", 1)
    add_heading(doc, "1 1 Основание проекта", 2)
    add_paragraph(
        doc,
        "Студия Bebra Beavers инициирует Burmalda Factory как самостоятельную игру в жанре автоматизации производства. Проект развивает опыт команды в создании системных игр и переводит его в продукт с законченным одиночным прохождением, измеримыми требованиями к качеству и публичным выпуском в Steam.",
    )
    add_paragraph(
        doc,
        "Бурмалда в мире игры является вымышленным промышленным материалом. Игрок строит предприятие, превращает исходные ресурсы в компоненты, связывает участки логистикой и постепенно переходит от ручных операций к автономному производству. Основной сценарий завершается запуском комплекса Тёмный Друн, после чего доступна свободная игра.",
    )

    add_heading(doc, "1 2 Назначение устава", 2)
    add_bullets(
        doc,
        [
            "подтвердить деловую и продуктовую цель проекта",
            "зафиксировать состав релизной версии и исключения из него",
            "определить критерии готовности продукта и правила приёмки",
            "установить ключевые сроки, бюджетные границы и порядок изменения базового плана",
            "назначить роли, ответственность и пределы полномочий руководителя проекта",
        ],
    )

    add_heading(doc, "1 3 Целевая аудитория", 2)
    add_paragraph(
        doc,
        "Основная аудитория — игроки от 14 лет, которым нравятся Factorio, Satisfactory, Shapez и другие игры о построении производственных систем. Продукт ориентирован на пользователей, которым важны понятная причинно следственная связь между действиями, возможность перестраивать производство без необратимого наказания и постепенный рост сложности. Базовый выпуск рассчитан на одиночное прохождение без обязательного сетевого подключения после установки.",
    )

    add_heading(doc, "2 Цели и ожидаемый результат", 1)
    add_heading(doc, "2 1 Цель проекта", 2)
    add_paragraph(
        doc,
        "К 19 ноября 2026 года разработать, протестировать и выпустить в Steam законченную версию Burmalda Factory для Windows, в которой игрок может пройти путь от ручной добычи до полностью автоматизированного производства и завершить проект Тёмный Друн.",
    )

    add_heading(doc, "2 2 Измеримые результаты", 2)
    add_table(
        doc,
        ["Результат", "Критерий подтверждения"],
        [
            ["Законченный игровой цикл", "Новая игра проходит от обучения до запуска Тёмного Друна без консольных команд и вмешательства разработчика"],
            ["Система автоматизации", "Игрок добывает, перерабатывает, хранит и транспортирует ресурсы с помощью построенных им цепочек"],
            ["Технологический прогресс", "Четыре производственные эпохи открываются последовательно и дают новые рецепты, здания и способы оптимизации"],
            ["Релизная сборка", "Сборка проходит приёмочное тестирование, подписана версией 1.0 и опубликована в Steam"],
            ["Материалы выпуска", "Подготовлены страница магазина, трейлер, скриншоты, описание, системные требования и руководство пользователя"],
            ["Эксплуатационная готовность", "Настроены сбор ошибок, резервное хранение исходного кода, выпуск исправлений и канал поддержки игроков"],
        ],
        widths=[4.8, 11.4],
        font_size=9.3,
    )

    add_heading(doc, "3 Концепция продукта", 1)
    add_heading(doc, "3 1 Игровой цикл", 2)
    add_numbered(
        doc,
        [
            "Исследовать доступную территорию и находить источники сырья.",
            "Добывать ресурсы вручную, затем заменять ручной труд добывающими установками.",
            "Перерабатывать сырьё в промежуточные материалы и готовые компоненты.",
            "Соединять участки конвейерами, трубопроводами, распределителями и складами.",
            "Производить исследовательские пакеты и открывать новые технологии.",
            "Устранять узкие места по данным о загрузке, расходе энергии и пропускной способности.",
            "Собрать компоненты финального комплекса и запустить Тёмный Друн.",
        ],
    )

    add_heading(doc, "3 2 Принципы игрового опыта", 2)
    add_bullets(
        doc,
        [
            "каждая производственная проблема должна иметь несколько понятных способов решения",
            "ошибки планировки должны исправляться перестройкой, а не обязательным перезапуском прохождения",
            "рост сложности должен происходить через новые связи между системами, а не только через увеличение чисел",
            "интерфейс должен объяснять остановку производства и показывать место возникновения дефицита",
            "финальная цель должна проверять освоение основных систем, а не требовать длительного ожидания без новых решений",
        ],
    )

    add_heading(doc, "3 3 Состав релизной версии", 2)
    add_table(
        doc,
        ["Область", "Минимальный состав версии 1 0"],
        [
            ["Мир", "Одна большая карта с настраиваемым начальным зерном, тремя природными зонами и ограничениями рельефа"],
            ["Ресурсы", "Не менее шести базовых ресурсов и тридцати производственных рецептов"],
            ["Строительство", "Не менее восемнадцати производственных и шести логистических объектов"],
            ["Логистика", "Конвейеры, развилки, объединители, склады, трубопроводы и транспорт для дальних маршрутов"],
            ["Энергетика", "Производство, распределение и контроль потребления энергии с понятными причинами отключения"],
            ["Прогресс", "Четыре эпохи технологий, дерево исследований и финальный комплекс Тёмный Друн"],
            ["Управление", "Строительство, демонтаж с возвратом материалов, массовое выделение, копирование конфигураций и карта производства"],
            ["Сохранения", "Ручные сохранения, три автоматически обновляемых слота и восстановление после некорректного завершения"],
            ["Доступность", "Переназначение клавиш, масштаб интерфейса, отдельные настройки громкости и режимы различения цветов"],
            ["Локализация", "Русский и английский языки интерфейса, обучения и описаний"],
        ],
        widths=[3.8, 12.4],
        font_size=9,
    )

    add_heading(doc, "4 Границы проекта", 1)
    add_heading(doc, "4 1 Входит в проект", 2)
    add_bullets(
        doc,
        [
            "проектирование правил, экономики, рецептов, технологий и финального сценария",
            "разработка игрового клиента, интерфейса, системы сохранений и инструментов диагностики",
            "создание трёхмерных моделей, анимаций, эффектов, звуков и музыки, необходимых для версии 1.0",
            "внутреннее тестирование, закрытые игровые сессии и исправление дефектов",
            "подготовка магазина Steam, достижений, облачных сохранений и релизной сборки",
            "подготовка документации по выпуску, поддержке и аварийному откату версии",
        ],
    )

    add_heading(doc, "4 2 Не входит в проект", 2)
    add_bullets(
        doc,
        [
            "многопользовательский режим и выделенные серверы",
            "версии для консолей, мобильных устройств, macOS и Linux",
            "официальный редактор модификаций и мастерская Steam",
            "сюжетная кампания с озвученными диалогами и постановочными сценами",
            "пользовательские серверы, внутриигровые покупки и сезонные пропуски",
            "дополнительные биомы и сюжетные главы после Тёмного Друна",
        ],
    )
    add_paragraph(
        doc,
        "Перечисленные возможности могут рассматриваться после выпуска версии 1.0. Их исследование не должно уменьшать объём тестирования и исправлений, предусмотренный базовым планом.",
    )

    add_heading(doc, "5 Требования и критерии приёмки", 1)
    add_heading(doc, "5 1 Функциональные требования", 2)
    add_table(
        doc,
        ["Код", "Требование", "Приёмочное условие"],
        [
            ["FR 01", "Строительство", "Игрок размещает, поворачивает, соединяет, настраивает и демонтирует объекты в разрешённой зоне"],
            ["FR 02", "Производство", "Каждый рецепт проверяет входы, энергию и выходное хранилище и сообщает причину остановки"],
            ["FR 03", "Логистика", "Ресурсы корректно проходят через развилки и объединители без дублирования и потери"],
            ["FR 04", "Исследования", "Технология открывается только после выполнения её условий и сохраняется между сеансами"],
            ["FR 05", "Сохранения", "Сохранение восстанавливает мир, инвентарь, здания, связи, прогресс и настройки без расхождений"],
            ["FR 06", "Финал", "После поставки всех компонентов запускается Тёмный Друн, фиксируется завершение и открывается свободная игра"],
            ["FR 07", "Обучение", "Новый игрок получает задания по добыче, строительству, логистике, энергии и исследованиям"],
            ["FR 08", "Steam", "Работают достижения, облачные сохранения и запуск из библиотеки Steam без внешнего лаунчера"],
        ],
        widths=[2.0, 3.7, 10.5],
        font_size=8.8,
        alignments=[WD_ALIGN_PARAGRAPH.CENTER, WD_ALIGN_PARAGRAPH.LEFT, WD_ALIGN_PARAGRAPH.LEFT],
    )

    add_heading(doc, "5 2 Нефункциональные требования", 2)
    add_table(
        doc,
        ["Категория", "Порог приёмки"],
        [
            ["Производительность", "Не менее 60 кадров в секунду при разрешении 1920 на 1080 на рекомендованной конфигурации в эталонной фабрике из 3000 активных объектов; не менее 30 кадров на минимальной конфигурации"],
            ["Стабильность", "Не менее 99,5 процента игровых сеансов завершаются без аварийного закрытия в итоговой выборке тестирования"],
            ["Сохранения", "Автоматическое сохранение занимает не более двух секунд и не блокирует управление более чем на 200 миллисекунд"],
            ["Совместимость", "Сборка проходит проверку на актуальных 64 разрядных версиях Windows 10 и Windows 11"],
            ["Качество релиза", "Открытых дефектов критического уровня нет; дефекты высокого уровня не блокируют основной игровой цикл и имеют согласованный план исправления"],
            ["Понятность", "Не менее 80 процентов участников внешнего теста завершают обучение без подсказки модератора"],
            ["Защита данных", "Телеметрия отключается в настройках; игра не передаёт персональные данные без явного согласия пользователя"],
        ],
        widths=[4.0, 12.2],
        font_size=9,
    )

    add_heading(doc, "5 3 Критерии успешности проекта", 2)
    add_table(
        doc,
        ["Измерение", "Критерий успеха", "Источник подтверждения"],
        [
            ["Содержание", "Все обязательные системы версии 1.0 интегрированы; Тёмный Друн достижим из нового сохранения", "Приёмочный сценарий и протокол прохождения"],
            ["Срок", "Публичный выпуск состоялся не позднее 19 ноября 2026 года", "Запись релиза в Steam и протокол решения о выпуске"],
            ["Бюджет", "Фактические затраты не превышают 420 000 BYN без отдельного решения спонсора", "Финансовый отчёт проекта"],
            ["Качество", "Выполнены нефункциональные пороги и отсутствуют критические дефекты", "Отчёт QA и результаты автоматизированных тестов"],
            ["Игровой опыт", "Медианное время основного прохождения внешней группы находится в диапазоне 20–35 часов", "Отчёт закрытого тестирования не менее чем на 20 участниках"],
            ["Готовность выпуска", "Страница магазина, локализация, поддержка и процедура отката проверены до публикации", "Чек лист релиза"],
        ],
        widths=[3.0, 8.1, 5.1],
        font_size=8.8,
    )

    add_heading(doc, "6 Результаты проекта", 1)
    add_table(
        doc,
        ["Результат", "Ответственный", "Основание приёмки"],
        [
            ["Концепция и игровой дизайн", "Ведущий геймдизайнер", "Утверждённые правила, балансные модели и карта прогресса"],
            ["Вертикальный срез", "Технический руководитель", "Один полный производственный цикл с финальным качеством графики и звука"],
            ["Контент версии 1.0", "Геймдизайн и арт", "Перечень ресурсов, рецептов, зданий, технологий, эффектов и текстов закрыт"],
            ["Релизная сборка", "Технический руководитель", "Подписанная сборка, воспроизводимый процесс сборки и контрольная сумма"],
            ["Отчёт о качестве", "Руководитель QA", "Результаты тестов, перечень известных ограничений и решение о выпуске"],
            ["Комплект Steam", "Продюсер", "Страница магазина, материалы, достижения, облачные сохранения и ценовые настройки"],
            ["План поддержки", "Руководитель проекта", "Каналы обращений, приоритеты дефектов, ответственные и порядок выпуска исправлений"],
        ],
        widths=[5.0, 4.0, 7.2],
        font_size=9,
    )

    add_heading(doc, "7 План контрольных событий", 1)
    add_table(
        doc,
        ["Контрольное событие", "Плановая дата", "Условие завершения"],
        [
            ["Инициация проекта", "17.09.2025", "Устав утверждён, руководитель назначен, команда сформирована"],
            ["Концепция и базовый дизайн", "10.10.2025", "Зафиксированы игровой цикл, аудитория, границы и прототипы интерфейса"],
            ["Игровой прототип", "28.11.2025", "Работают добыча, строительство, один рецепт и базовый конвейер"],
            ["Вертикальный срез", "27.02.2026", "Один участок игры представлен с целевым качеством и проходит внутреннюю оценку"],
            ["Альфа версия", "29.05.2026", "Основные системы реализованы, прохождение возможно с временными материалами"],
            ["Контент готов", "31.07.2026", "Новые обязательные функции не добавляются, список контента закрыт"],
            ["Бета версия", "28.08.2026", "Содержание версии 1.0 собрано, начато внешнее тестирование и оптимизация"],
            ["Релиз кандидат", "23.10.2026", "Критические дефекты устранены, материалы Steam готовы, выполнена репетиция выпуска"],
            ["Публичный выпуск", "19.11.2026", "Версия 1.0 опубликована и работает мониторинг обращений"],
            ["Закрытие проекта", "18.12.2026", "Подписан итоговый отчёт, материалы переданы команде поддержки"],
        ],
        widths=[4.5, 3.0, 8.7],
        font_size=8.8,
        alignments=[WD_ALIGN_PARAGRAPH.LEFT, WD_ALIGN_PARAGRAPH.CENTER, WD_ALIGN_PARAGRAPH.LEFT],
    )

    add_heading(doc, "8 Бюджет и ресурсы", 1)
    add_heading(doc, "8 1 Базовый бюджет", 2)
    add_table(
        doc,
        ["Статья", "Сумма BYN", "Комментарий"],
        [
            ["Труд команды", "310 000", "Разработка, дизайн, графика, звук, тестирование и управление"],
            ["Программное обеспечение и инфраструктура", "30 000", "Лицензии, хранение, сборки, тестовые устройства и сервисы"],
            ["Внешние работы", "32 000", "Локализация, трейлер, мастеринг звука и точечная экспертиза"],
            ["Подготовка и продвижение выпуска", "12 000", "Комиссии, материалы магазина, тестовые кампании и коммуникации"],
            ["Управленческий резерв", "36 000", "Риски производительности, совместимости и незапланированные исправления"],
            ["Итого", "420 000", "Предельный бюджет без отдельного решения спонсора"],
        ],
        widths=[5.0, 3.0, 8.2],
        font_size=9,
        alignments=[WD_ALIGN_PARAGRAPH.LEFT, WD_ALIGN_PARAGRAPH.CENTER, WD_ALIGN_PARAGRAPH.LEFT],
    )

    add_heading(doc, "8 2 Команда", 2)
    add_table(
        doc,
        ["Роль", "Загрузка", "Основная ответственность"],
        [
            ["Руководитель проекта", "1 человек", "Сроки, бюджет, риски, изменения, отчётность и выпуск"],
            ["Ведущий геймдизайнер", "1 человек", "Игровой цикл, экономика, прогресс, обучение и баланс"],
            ["Технический руководитель", "1 человек", "Архитектура, производительность, сборки и техническое качество"],
            ["Программисты", "2 человека", "Игровые системы, интерфейс, инструменты и интеграция Steam"],
            ["Художник и специалист по интерфейсу", "1 человек", "Визуальный язык, модели, эффекты, интерфейс и материалы Steam"],
            ["Звук и музыка", "0,5 ставки", "Звуковые эффекты, музыка и мастеринг"],
            ["Тестирование", "1 человек и внешняя группа", "План качества, регрессия, совместимость и игровые сессии"],
        ],
        widths=[4.5, 2.8, 8.9],
        font_size=9,
    )

    add_heading(doc, "9 Ограничения и допущения", 1)
    add_heading(doc, "9 1 Ограничения", 2)
    add_bullets(
        doc,
        [
            "дата публичного выпуска закреплена на 19 ноября 2026 года",
            "бюджет ограничен суммой 420 000 BYN до отдельного решения спонсора",
            "команда работает удалённо и не может опираться на постоянный доступ к общей локальной инфраструктуре",
            "базовый выпуск создаётся для Windows и Steam с использованием одного игрового движка и единого конвейера сборки",
            "после даты готовности контента новые обязательные функции принимаются только взамен сопоставимого объёма работ",
            "лицензии на сторонние материалы должны разрешать коммерческое распространение и храниться в реестре проекта",
        ],
    )

    add_heading(doc, "9 2 Допущения", 2)
    add_bullets(
        doc,
        [
            "ключевые специалисты доступны в объёме, указанном в ресурсном плане",
            "Steam сохраняет текущую процедуру публикации и предоставляет необходимые сервисы до даты выпуска",
            "игровой движок поддерживает целевые версии Windows и не требует смены основной технологии",
            "внешние тестировщики доступны не позднее начала бета этапа",
            "заказчик рассматривает решения по изменениям и выпуску не более пяти рабочих дней",
            "перечень языков версии 1.0 ограничивается русским и английским",
        ],
    )

    add_heading(doc, "10 Основные риски", 1)
    add_table(
        doc,
        ["Риск", "Вероятность", "Влияние", "Меры реагирования", "Владелец"],
        [
            ["Снижение производительности на крупных фабриках", "Высокая", "Высокое", "Профилирование с прототипа, эталонная сцена, бюджет объектов и нагрузочные тесты каждого этапа", "Технический руководитель"],
            ["Расширение состава версии 1.0", "Высокая", "Высокое", "Закрытие контента 31 июля, обязательная оценка обмена объёма, резерв только по решению спонсора", "Руководитель проекта"],
            ["Нарушение сохранений после обновлений", "Средняя", "Высокое", "Версионирование формата, миграционные тесты, резервные автосохранения и набор эталонных файлов", "Ведущий программист"],
            ["Сложность игры непонятна новым пользователям", "Средняя", "Высокое", "Прототип обучения, наблюдаемые тесты, аналитика шагов и исправления до беты", "Ведущий геймдизайнер"],
            ["Недостаток контента между эпохами", "Средняя", "Среднее", "Карта темпа прогресса, тесты времени до технологий, приоритет уникальных решений вместо повторяющихся рецептов", "Ведущий геймдизайнер"],
            ["Задержка графики или звука", "Средняя", "Среднее", "Приоритет обязательных материалов, повторное использование модулей, резерв внешнего производства", "Продюсер"],
            ["Критические дефекты перед выпуском", "Средняя", "Высокое", "Запрет новых функций после беты, ежедневная регрессия, репетиция релиза и готовый план отката", "Руководитель QA"],
            ["Неготовность материалов Steam", "Низкая", "Среднее", "Черновик страницы до беты, отдельный чек лист, проверка возрастных и локализационных данных", "Продюсер"],
        ],
        widths=[3.3, 2.1, 1.8, 6.2, 2.8],
        font_size=8.1,
        alignments=[WD_ALIGN_PARAGRAPH.LEFT, WD_ALIGN_PARAGRAPH.CENTER, WD_ALIGN_PARAGRAPH.CENTER, WD_ALIGN_PARAGRAPH.LEFT, WD_ALIGN_PARAGRAPH.LEFT],
    )

    add_heading(doc, "11 Управление проектом", 1)
    add_heading(doc, "11 1 Принятие решений", 2)
    add_table(
        doc,
        ["Вопрос", "Решение", "Срок рассмотрения"],
        [
            ["Ежедневные технические решения в утверждённой архитектуре", "Технический руководитель", "В рабочем порядке"],
            ["Баланс и правила в утверждённой концепции", "Ведущий геймдизайнер", "До двух рабочих дней"],
            ["Перераспределение работ без изменения базовых сроков и бюджета", "Руководитель проекта", "До двух рабочих дней"],
            ["Изменение обязательного состава версии 1.0", "Спонсор по представлению руководителя проекта", "До пяти рабочих дней"],
            ["Использование управленческого резерва", "Спонсор", "До пяти рабочих дней"],
            ["Решение о публичном выпуске", "Спонсор, руководитель проекта, технический руководитель и руководитель QA", "Не позднее чем за три рабочих дня до выпуска"],
        ],
        widths=[7.1, 5.8, 3.3],
        font_size=8.8,
    )

    add_heading(doc, "11 2 Контроль изменений", 2)
    add_paragraph(
        doc,
        "Изменение регистрируется, если оно влияет на обязательную функцию, контрольную дату, бюджетную статью, критерий качества или внешний договор. Инициатор описывает причину, ожидаемую ценность и последствия отказа. Руководитель проекта организует оценку трудозатрат, рисков и влияния на релиз, после чего принимает решение в пределах своих полномочий или передаёт вопрос спонсору.",
    )
    add_paragraph(
        doc,
        "После даты готовности контента изменение допускается только при устранении дефекта, выполнении обязательного требования Steam или обмене на работы сопоставимого объёма. Утверждённое изменение вносится в план, журнал решений, перечень требований и релизный чек лист.",
    )

    add_heading(doc, "11 3 Отчётность и коммуникации", 2)
    add_table(
        doc,
        ["Формат", "Участники", "Периодичность", "Результат"],
        [
            ["Планирование недели", "Вся команда", "Еженедельно", "Приоритеты, владельцы и ожидаемые результаты недели"],
            ["Демонстрация сборки", "Команда и координатор заказчика", "Раз в две недели", "Принятые результаты и перечень решений"],
            ["Отчёт о состоянии", "Руководитель проекта и спонсор", "Ежемесячно", "Сроки, бюджет, качество, риски и запросы на решение"],
            ["Разбор качества", "QA, разработка, геймдизайн", "Еженедельно с альфа этапа", "Приоритет дефектов и готовность сборки"],
            ["Решение о выпуске", "Спонсор и руководители направлений", "Однократно для версии 1.0", "Подписанный протокол выпуска или перенос с условиями"],
        ],
        widths=[4.0, 4.3, 3.2, 4.7],
        font_size=8.7,
    )

    add_heading(doc, "12 Полномочия руководителя проекта", 1)
    add_paragraph(
        doc,
        "Руководителем проекта назначен Кучерук Николай. Назначение действует с даты утверждения устава до подписания итогового отчёта либо отдельного решения спонсора.",
    )
    add_heading(doc, "12 1 Полномочия", 2)
    add_bullets(
        doc,
        [
            "назначать владельцев задач и согласовывать приоритет работ команды",
            "утверждать рабочие планы и перестраивать последовательность задач без изменения базовых контрольных дат",
            "перераспределять средства между утверждёнными статьями в пределах 5 000 BYN на одно решение и 15 000 BYN суммарно за месяц",
            "приостанавливать включение новых функций, если они угрожают сроку выпуска или критериям качества",
            "запрашивать у руководителей направлений оценки, отчёты, демонстрации и планы исправления отклонений",
            "эскалировать спонсору изменения содержания, бюджета, срока и критические ресурсные конфликты",
        ],
    )
    add_heading(doc, "12 2 Ответственность", 2)
    add_bullets(
        doc,
        [
            "поддерживать единый базовый план и актуальный журнал решений",
            "не скрывать отклонения, которые угрожают контрольным событиям или критериям приёмки",
            "обеспечивать управление рисками, качеством, изменениями и коммуникациями",
            "готовить решение о выпуске на основании результатов тестирования, а не календарной даты",
            "организовать передачу продукта, исходного кода, сборочного процесса и документов команде поддержки",
        ],
    )

    add_heading(doc, "13 Порядок приёмки и закрытия", 1)
    add_numbered(
        doc,
        [
            "Руководитель QA подтверждает выполнение приёмочных сценариев и публикует перечень открытых дефектов.",
            "Технический руководитель подтверждает воспроизводимость сборки, производительность, сохранения и готовность отката.",
            "Ведущий геймдизайнер подтверждает достижимость финальной цели и выполнение требований к прогрессу и обучению.",
            "Руководитель проекта сверяет содержание, сроки, бюджет, риски и материалы Steam и формирует рекомендацию о выпуске.",
            "Спонсор принимает решение о выпуске, переносе либо исключении конкретных требований с фиксацией последствий.",
            "После выпуска команда в течение четырёх недель отслеживает критические обращения и выпускает необходимые исправления.",
            "Проект закрывается после передачи материалов, сверки затрат, фиксации уроков и утверждения итогового отчёта.",
        ],
    )

    add_heading(doc, "14 Условия действия устава", 1)
    add_paragraph(
        doc,
        "Устав вступает в силу после утверждения заказчиком и спонсором. Все планы нижнего уровня должны соответствовать его целям, границам и критериям приёмки. При противоречии между рабочей задачей и уставом применяется устав либо оформляется изменение в установленном порядке.",
    )
    add_paragraph(
        doc,
        "Электронное согласование в системе управления проектом признаётся равнозначным подписи на бумажном экземпляре при наличии имени согласующего, даты и неизменяемой версии документа.",
    )

    add_heading(doc, "Лист утверждения", 1)
    add_table(
        doc,
        ["Роль", "ФИО", "Подпись", "Дата"],
        [
            ["Заказчик и спонсор", "Кабан Кабаныч", "________________", "________________"],
            ["Координатор заказчика", "Андрей Манагерович", "________________", "________________"],
            ["Руководитель проекта", "Кучерук Николай", "________________", "________________"],
        ],
        widths=[4.5, 4.3, 4.2, 3.2],
        font_size=9,
        alignments=[WD_ALIGN_PARAGRAPH.LEFT, WD_ALIGN_PARAGRAPH.LEFT, WD_ALIGN_PARAGRAPH.CENTER, WD_ALIGN_PARAGRAPH.CENTER],
    )
    add_paragraph(
        doc,
        "Контрольный экземпляр хранится в электронном репозитории проекта. Печатные копии считаются неконтролируемыми после выпуска новой утверждённой редакции.",
        style="Small Text",
    )

    return doc


def main() -> None:
    OUTPUT.parent.mkdir(parents=True, exist_ok=True)
    doc = build_document()
    doc.save(OUTPUT)
    print(OUTPUT)


if __name__ == "__main__":
    main()
