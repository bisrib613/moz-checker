"""Desktop presentation primitives: local vector icons and shared semantic theme tokens."""
from PySide6.QtCore import Qt, QSize, QByteArray, QPointF
from PySide6.QtGui import QIcon, QPainter, QPixmap, QPalette, QColor, QPen
from PySide6.QtSvg import QSvgRenderer
from PySide6.QtWidgets import (QFrame, QLabel, QPushButton, QVBoxLayout, QComboBox,
                               QSpinBox, QDoubleSpinBox, QStyle, QStyleOptionSpinBox)

COLORS = {
    'bg': '#0c1216', 'panel': '#111b22', 'raised': '#17232d', 'input': '#0d171f',
    'border': '#344956', 'text': '#e8f0f5', 'muted': '#a4b6c4',
    'accent': '#8be6b3', 'accent_text': '#11231a', 'danger': '#ff9b9b', 'blue': '#9ecbff',
}

STYLE = '''
QWidget { color: @text; font-family: "Segoe UI", "DejaVu Sans", sans-serif; font-size: 13px; }
QMainWindow, QDialog, QWidget#workspace { background: @bg; }
QFrame#topbar { background: @panel; border-bottom: 1px solid @border; }
QFrame#sidebar, QFrame#card, QFrame#stat, QFrame#details { background: @panel; border: 1px solid @border; border-radius: 12px; }
QFrame#statHighlight { background: #142a23; border: 1px solid #477b61; border-radius: 12px; }
QFrame#quotaCard { background: #15242a; border: 1px solid @border; border-radius: 10px; }
QLabel { background: transparent; }
QLabel#wordmark { font-size: 19px; font-weight: 700; letter-spacing: -0.3px; }
QLabel#brandmark { background: @accent; color: @accent_text; border-radius: 10px; font-size: 23px; font-weight: 800; }
QLabel#eyebrow { color: @accent; font-size: 11px; font-weight: 700; letter-spacing: 1.4px; }
QLabel#title { font-size: 25px; font-weight: 700; letter-spacing: -0.6px; }
QLabel#sectionTitle { font-size: 18px; font-weight: 600; }
QLabel#subtle, QLabel#hint { color: @muted; }
QLabel#hint { font-size: 12px; }
QLabel#fieldLabel { color: #cbd8e2; font-size: 12px; font-weight: 600; }
QLabel#statValue { font-size: 30px; font-weight: 700; }
QFrame#statHighlight QLabel#statValue { color: @accent; }
QLabel#statCaption { color: @muted; font-size: 11px; }
QLabel#localBadge { background: #1a2d29; color: #b4e7cb; border: 1px solid #3b6252; padding: 6px 10px; border-radius: 6px; font-size: 12px; }
QLineEdit, QPlainTextEdit, QComboBox, QSpinBox, QDoubleSpinBox {
 background: @input; color: @text; border: 1px solid @border; border-radius: 6px; padding: 8px; selection-background-color: #335f78; }
QLineEdit:focus, QPlainTextEdit:focus, QComboBox:focus, QSpinBox:focus, QDoubleSpinBox:focus { border: 1px solid @accent; }
QPlainTextEdit#domainInput, QPlainTextEdit#log, QPlainTextEdit#evidence { font-family: "Cascadia Code", "Consolas", "DejaVu Sans Mono", monospace; font-size: 12px; }
QComboBox::drop-down { border: 0; width: 22px; }
QComboBox QAbstractItemView { background: @raised; color: @text; selection-background-color: #315367; }
QPushButton, QToolButton { background: @raised; border: 1px solid @border; border-radius: 6px; padding: 8px 12px; min-height: 20px; font-weight: 500; }
QPushButton:hover, QToolButton:hover { background: #223542; border-color: #6b899d; }
QPushButton:pressed, QToolButton:pressed { background: #2c4554; }
QPushButton:focus, QToolButton:focus { border: 2px solid @accent; padding: 7px 11px; }
QPushButton:disabled, QToolButton:disabled { color: #8796a3; background: #151f27; border-color: #2b3e4c; }
QPushButton#primary { color: @accent_text; background: @accent; border-color: @accent; font-weight: 700; }
QPushButton#primary:hover { background: #b1f4cd; }
QPushButton#primary:pressed { background: #66c891; }
QPushButton#primary:focus { border: 2px solid #ffffff; }
QPushButton#primary:disabled { color: #a4b6c4; background: #243b32; border-color: #39574a; }
QToolButton#disclosure { border: 0; text-align: left; background: transparent; color: @muted; }
QToolButton#disclosure:focus { border: 1px solid @accent; }
QToolButton:checked { color: @accent; border-color: #477b61; }
QTableWidget { background: @panel; alternate-background-color: #14212a; gridline-color: #233642; border: 0; selection-background-color: #234737; selection-color: @text; }
QTableWidget::item { border-bottom: 1px solid #233642; padding: 6px; }
QTableWidget::item:focus { border: 1px solid @accent; }
QHeaderView::section { background: @raised; color: #b5c7d5; font-size: 11px; font-weight: 600; border: 0; border-bottom: 1px solid @border; padding: 12px 8px; }
QTableCornerButton::section { background: @raised; border: 0; }
QProgressBar { background: #253540; border: 0; border-radius: 3px; height: 6px; color: @muted; }
QProgressBar::chunk { background: @accent; border-radius: 3px; }
QTabWidget::pane { border: 0; background: @panel; }
QWidget#resultsPage, QWidget#inputPage, QWidget#settingsPage { background: @panel; }
QTabBar::tab { background: transparent; color: @muted; border: 0; border-bottom: 2px solid transparent; padding: 12px 16px; font-weight: 600; }
QTabBar::tab:selected { color: @accent; border-bottom: 2px solid @accent; }
QTabBar::tab:hover { color: @text; background: @raised; }
QTabBar::tab:focus { border: 1px solid @accent; }
QScrollArea { border: 0; background: transparent; }
QScrollBar:vertical { background: @bg; width: 9px; margin: 0; }
QScrollBar::handle:vertical { background: #435d6b; min-height: 28px; border-radius: 4px; }
QScrollBar:horizontal { background: @bg; height: 9px; margin: 0; }
QScrollBar::handle:horizontal { background: #435d6b; min-width: 28px; border-radius: 4px; }
QScrollBar::add-line, QScrollBar::sub-line { width: 0; height: 0; }
QCheckBox { spacing: 8px; }
QCheckBox::indicator { width: 18px; height: 18px; }
QCheckBox:focus { outline: 1px solid @accent; }
QStatusBar { background: @bg; color: @muted; font-size: 12px; }
QToolTip { background: @raised; color: @text; border: 1px solid @border; padding: 6px; }
'''
for token in sorted(COLORS, key=len, reverse=True):
    color = COLORS[token]
    STYLE = STYLE.replace('@' + token, color)

PATHS = {
    'search': '<circle cx="10.5" cy="10.5" r="6.5"/><path d="m16 16 5 5"/>',
    'play': '<path d="m8 5 11 7-11 7z"/>',
    'pause': '<path d="M8 5v14M16 5v14"/>',
    'upload': '<path d="M12 16V3m-5 5 5-5 5 5M4 16v5h16v-5"/>',
    'download': '<path d="M12 3v13m-5-5 5 5 5-5M4 16v5h16v-5"/>',
    'refresh': '<path d="M20 7v5h-5M4 17v-5h5"/><path d="M6 7a7 7 0 0 1 12-1l2 3M4 15l2 3a7 7 0 0 0 12-1"/>',
    'filter': '<path d="M3 5h18l-7 8v6l-4 2v-8z"/>',
    'settings': '<path d="M4 7h16M4 17h16"/><circle cx="9" cy="7" r="3"/><circle cx="15" cy="17" r="3"/>',
    'arrow': '<path d="m9 5 7 7-7 7"/>',
    'globe': '<circle cx="12" cy="12" r="9"/><ellipse cx="12" cy="12" rx="4" ry="9"/><path d="M3 12h18"/>',
    'check': '<path d="m5 12 4 4L19 6"/>',
}


def icon(name, color=None, size=20):
    svg = ('<svg xmlns="http://www.w3.org/2000/svg" viewBox="0 0 24 24" fill="none" '
           f'stroke="{color or COLORS["muted"]}" stroke-width="1.7" stroke-linecap="round" '
           f'stroke-linejoin="round">{PATHS[name]}</svg>')
    pixmap = QPixmap(size * 2, size * 2)
    pixmap.fill(Qt.GlobalColor.transparent)
    painter = QPainter(pixmap)
    QSvgRenderer(QByteArray(svg.encode())).render(painter)
    painter.end()
    pixmap.setDevicePixelRatio(2)
    return QIcon(pixmap)


def label(text, role='subtle', wrap=False):
    widget = QLabel(text)
    widget.setObjectName(role)
    widget.setWordWrap(wrap)
    return widget


def button(text, callback, symbol=None, primary=False):
    widget = QPushButton(text)
    widget.setCursor(Qt.CursorShape.PointingHandCursor)
    widget.setMinimumHeight(38)
    if symbol:
        widget.setIcon(icon(symbol, COLORS['accent_text'] if primary else None))
        widget.setIconSize(QSize(18, 18))
    if primary:
        widget.setObjectName('primary')
    widget.clicked.connect(callback)
    return widget


class StatCard(QFrame):
    def __init__(self, title, caption, highlight=False):
        super().__init__()
        self.setObjectName('statHighlight' if highlight else 'stat')
        layout = QVBoxLayout(self)
        layout.setContentsMargins(16, 12, 16, 12)
        layout.setSpacing(4)
        layout.addWidget(label(title, 'fieldLabel'))
        self.value = label('0', 'statValue')
        layout.addWidget(self.value)
        self.caption = label(caption, 'statCaption')
        layout.addWidget(self.caption)
        self.title = title

    def set_value(self, value):
        self.value.setText(str(value))
        self.setAccessibleName(f'{self.title}: {value}')


def apply_theme(app):
    app.setStyle('Fusion')
    palette = QPalette()
    for role, value in {
        QPalette.ColorRole.Window: COLORS['bg'], QPalette.ColorRole.WindowText: COLORS['text'],
        QPalette.ColorRole.Base: COLORS['input'], QPalette.ColorRole.AlternateBase: COLORS['panel'],
        QPalette.ColorRole.Text: COLORS['text'], QPalette.ColorRole.Button: COLORS['raised'],
        QPalette.ColorRole.ButtonText: COLORS['text'], QPalette.ColorRole.ToolTipBase: COLORS['raised'],
        QPalette.ColorRole.ToolTipText: COLORS['text'], QPalette.ColorRole.Highlight: '#234737',
        QPalette.ColorRole.HighlightedText: COLORS['text'], QPalette.ColorRole.PlaceholderText: COLORS['muted'],
        QPalette.ColorRole.Light: '#607a8a', QPalette.ColorRole.Mid: COLORS['border'],
        QPalette.ColorRole.Dark: COLORS['bg'], QPalette.ColorRole.BrightText: COLORS['danger'],
    }.items():
        palette.setColor(role, QColor(value))
    app.setPalette(palette)
    app.setStyleSheet(STYLE)


class ComboBox(QComboBox):
    def paintEvent(self, event):
        super().paintEvent(event)
        painter = QPainter(self)
        painter.setRenderHint(QPainter.RenderHint.Antialiasing)
        painter.setPen(QPen(QColor(COLORS['muted']), 1.7))
        x, y = self.width() - 14, self.height() / 2
        painter.drawPolyline([QPointF(x - 4, y - 2), QPointF(x, y + 2), QPointF(x + 4, y - 2)])
        painter.end()


def paint_steps(widget):
    option = QStyleOptionSpinBox()
    widget.initStyleOption(option)
    painter = QPainter(widget)
    painter.setRenderHint(QPainter.RenderHint.Antialiasing)
    painter.setPen(QPen(QColor(COLORS['muted']), 1.5))
    for control, direction in [(QStyle.SubControl.SC_SpinBoxUp, -1), (QStyle.SubControl.SC_SpinBoxDown, 1)]:
        center = widget.style().subControlRect(QStyle.ComplexControl.CC_SpinBox, option, control, widget).center()
        x, y = center.x(), center.y()
        painter.drawPolyline([QPointF(x - 3, y - direction), QPointF(x, y + direction * 2),
                              QPointF(x + 3, y - direction)])
    painter.end()


class SpinBox(QSpinBox):
    def paintEvent(self, event):
        super().paintEvent(event)
        paint_steps(self)


class DoubleSpinBox(QDoubleSpinBox):
    def paintEvent(self, event):
        super().paintEvent(event)
        paint_steps(self)
