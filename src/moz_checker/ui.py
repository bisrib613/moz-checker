from datetime import datetime, timezone
import json
from pathlib import Path

from PySide6.QtCore import Qt, QTimer, QUrl, QLockFile
from PySide6.QtGui import QColor, QDesktopServices, QKeySequence, QShortcut
from PySide6.QtWidgets import (
    QAbstractItemView, QApplication, QCheckBox, QComboBox, QDialog, QDialogButtonBox,
    QFileDialog, QFormLayout, QGridLayout, QHBoxLayout, QHeaderView,
    QLabel, QLineEdit, QMainWindow, QMessageBox, QPlainTextEdit, QProgressBar,
    QTableWidget, QTableWidgetItem, QTabWidget, QVBoxLayout, QWidget,
    QFrame, QScrollArea, QToolButton, QSizePolicy,
)

from . import secrets
from .engine import Engine
from .models import parse_domains
from .store import Store, data_dir, export_csv

from .presentation import STYLE as STYLE
from .presentation import COLORS, StatCard, apply_theme, button, icon, label, ComboBox, SpinBox, DoubleSpinBox


class Settings(QDialog):
    def __init__(self, store, parent=None):
        super().__init__(parent)
        self.store = store
        self.setWindowTitle('Pengaturan workspace')
        self.resize(700, 640)
        self.setMinimumSize(560, 480)
        layout = QVBoxLayout(self)
        layout.setContentsMargins(24, 24, 24, 20)
        layout.setSpacing(16)
        layout.addWidget(label('Pengaturan workspace', 'title'))
        layout.addWidget(label('Key disimpan di credential vault sistem. Data hunting tetap berada di perangkat ini.', 'subtle', True))
        self.settings_tabs = QTabWidget()
        api_page = QWidget()
        api_page.setObjectName('settingsPage')
        api_layout = QVBoxLayout(api_page)
        api_layout.setContentsMargins(4, 16, 4, 12)
        api_layout.setSpacing(10)
        api_layout.addWidget(label('API key opsional', 'sectionTitle'))
        api_layout.addWidget(label('Tanpa key, pemeriksaan RDAP, DNS, dan Wayback tetap dapat berjalan. '
                                   'Kolom kosong mempertahankan key yang sudah tersimpan.', 'subtle', True))
        api_form = QFormLayout()
        api_form.setVerticalSpacing(14)
        api_form.setRowWrapPolicy(QFormLayout.RowWrapPolicy.WrapLongRows)
        self.keys, self.deletes = {}, {}
        for name, caption in [('opr_key', 'Open PageRank key'), ('moz_id', 'Moz Access ID'), ('moz_secret', 'Moz Secret')]:
            field = QLineEdit()
            field.setEchoMode(QLineEdit.EchoMode.Password)
            field.setAccessibleName(caption)
            field.setPlaceholderText('Tersimpan · kosongkan untuk mempertahankan' if secrets.get(name) else 'Belum diatur')
            delete = QCheckBox('Hapus key ini')
            delete.setAccessibleName('Hapus ' + caption)
            box = QWidget()
            box_layout = QVBoxLayout(box)
            box_layout.setContentsMargins(0, 0, 0, 0)
            box_layout.setSpacing(6)
            box_layout.addWidget(field)
            box_layout.addWidget(delete)
            api_form.addRow(caption, box)
            self.keys[name], self.deletes[name] = field, delete
        api_layout.addLayout(api_form)
        api_layout.addWidget(label('Moz memakai Links API v2: Access ID + Secret. Token JSON-RPC v3 belum didukung.', 'hint', True))
        api_layout.addStretch()
        self.settings_tabs.addTab(api_page, 'API & keamanan')
        rule_page = QWidget()
        rule_page.setObjectName('settingsPage')
        rule_layout = QVBoxLayout(rule_page)
        rule_layout.setContentsMargins(4, 16, 4, 12)
        rule_layout.addWidget(label('Aturan hunting', 'sectionTitle'))
        rule_layout.addWidget(label('Pengaturan berlaku untuk hunting berikutnya. Batas rows adalah penghitung lokal, bukan saldo akun Moz.', 'subtle', True))
        settings = store.settings()
        self.budget = SpinBox()
        self.budget.setRange(0, 10000000)
        self.budget.setValue(settings['moz_budget'])
        self.reserve = SpinBox()
        self.reserve.setRange(0, 10000000)
        self.reserve.setValue(settings['moz_reserve'])
        self.samples = SpinBox()
        self.samples.setRange(1, 25)
        self.samples.setValue(settings['samples'])
        self.opr = DoubleSpinBox()
        self.opr.setRange(0, 10)
        self.opr.setValue(settings['opr_min'])
        self.period = QLineEdit(settings['moz_period'])
        self.period.setPlaceholderText('Kosong = bulan UTC berjalan')
        self.empty = QCheckBox('Anggap valid jika tidak ada snapshot')
        self.empty.setChecked(settings['empty_valid'])
        self.keywords = QPlainTextEdit('\n'.join(settings['keywords']))
        self.keywords.setMinimumHeight(90)
        self.keywords.setMaximumHeight(130)
        form = QFormLayout()
        form.setVerticalSpacing(12)
        form.setRowWrapPolicy(QFormLayout.RowWrapPolicy.WrapLongRows)
        for caption, field in [('Batas rows / periode', self.budget), ('Cadangan rows', self.reserve),
                               ('ID periode tagihan', self.period), ('Sampel Wayback', self.samples),
                               ('OPR minimum untuk arsip', self.opr), ('Tanpa riwayat', self.empty),
                               ('Kata kunci (satu per baris)', self.keywords)]:
            field.setAccessibleName(caption)
            form.addRow(caption, field)
        rule_layout.addLayout(form)
        rule_layout.addStretch()
        for page in [api_page, rule_page]:
            scroll = QScrollArea()
            scroll.setWidgetResizable(True)
            scroll.setWidget(page)
            if page == api_page:
                self.settings_tabs.removeTab(0)
                self.settings_tabs.addTab(scroll, 'API & keamanan')
            else:
                self.settings_tabs.addTab(scroll, 'Aturan hunting')
        layout.addWidget(self.settings_tabs, 1)
        buttons = QDialogButtonBox(QDialogButtonBox.StandardButton.Save | QDialogButtonBox.StandardButton.Cancel)
        buttons.button(QDialogButtonBox.StandardButton.Save).setText('Simpan pengaturan')
        buttons.button(QDialogButtonBox.StandardButton.Cancel).setText('Batal')
        buttons.accepted.connect(self.persist)
        buttons.rejected.connect(self.reject)
        layout.addWidget(buttons)

    def persist(self):
        if self.reserve.value() > self.budget.value():
            QMessageBox.warning(self, 'Kuota', 'Cadangan tidak boleh melebihi batas rows.')
            return
        words = [w.strip() for w in self.keywords.toPlainText().splitlines() if w.strip()]
        if not words:
            QMessageBox.warning(self, 'Kata kunci', 'Isi minimal satu kata kunci untuk pemindaian arsip.')
            return
        try:
            for name, field in self.keys.items():
                if self.deletes[name].isChecked():
                    secrets.save(name, '')
                elif field.text().strip():
                    secrets.save(name, field.text().strip())
        except Exception:
            QMessageBox.warning(self, 'Penyimpanan key', 'Key gagal disimpan di credential vault sistem. '
                                'Tidak ada fallback ke berkas teks. Gunakan Windows Credential Manager.')
            return
        self.store.configure({'moz_budget': self.budget.value(), 'moz_reserve': self.reserve.value(),
                              'moz_period': self.period.text().strip(), 'samples': self.samples.value(),
                              'opr_min': self.opr.value(), 'empty_valid': self.empty.isChecked(), 'keywords': words})
        self.accept()


class NumericItem(QTableWidgetItem):
    def __lt__(self, other):
        left, right = self.data(Qt.ItemDataRole.UserRole), other.data(Qt.ItemDataRole.UserRole)
        if isinstance(left, (float, int)) and isinstance(right, (float, int)):
            return left < right
        return self.text().casefold() < other.text().casefold()


class Window(QMainWindow):
    columns = [('Domain', 'domain'), ('Status', 'status'), ('OPR', 'opr'), ('Moz DA', 'da'),
               ('Moz PA', 'pa'), ('Moz Spam', 'spam'), ('Riwayat', 'history'), ('Snapshot', 'snapshots'),
               ('Kedaluwarsa', 'expires'), ('Registrar', 'registrar'), ('Tahap', 'stage'), ('Moz', 'moz_state')]

    def __init__(self, store=None):
        super().__init__()
        self.store = store or Store(data_dir() / 'checker.sqlite3')
        self.engine = None
        self.closing_jobs = None
        self.current = None
        self.setWindowTitle('Moz Checker • Expired Domain Research')
        self._evidence_domain = None
        self.resize(1440, 900)
        self.setMinimumSize(1040, 700)
        root = QWidget()
        root.setObjectName('workspace')
        self.setCentralWidget(root)
        layout = QVBoxLayout(root)
        layout.setContentsMargins(0, 0, 0, 0)
        layout.setSpacing(0)

        topbar = QFrame()
        topbar.setObjectName('topbar')
        heading = QHBoxLayout(topbar)
        heading.setContentsMargins(24, 16, 24, 16)
        heading.setSpacing(12)
        brand = label('m', 'brandmark')
        brand.setFixedSize(40, 40)
        brand.setAlignment(Qt.AlignmentFlag.AlignCenter)
        heading.addWidget(brand)
        brand_text = QVBoxLayout()
        brand_text.setSpacing(0)
        brand_text.addWidget(label('Moz Checker', 'wordmark'))
        brand_text.addWidget(label('EXPIRED DOMAIN RESEARCH', 'hint'))
        heading.addLayout(brand_text)
        heading.addStretch()
        heading.addWidget(label('Data tersimpan lokal', 'localBadge'))
        heading.addWidget(button('Pengaturan API', self.open_settings, 'settings'))
        layout.addWidget(topbar)

        body = QHBoxLayout()
        body.setContentsMargins(20, 20, 20, 8)
        body.setSpacing(20)
        sidebar_scroll = QScrollArea()
        sidebar_scroll.setWidgetResizable(True)
        sidebar_scroll.setMinimumWidth(286)
        sidebar_scroll.setMaximumWidth(320)
        sidebar_scroll.setHorizontalScrollBarPolicy(Qt.ScrollBarPolicy.ScrollBarAlwaysOff)
        sidebar = QFrame()
        sidebar.setObjectName('sidebar')
        side = QVBoxLayout(sidebar)
        side.setContentsMargins(18, 20, 18, 18)
        side.setSpacing(12)
        side.addWidget(label('MULAI DARI SINI', 'eyebrow'))
        side.addWidget(label('Hunting baru', 'sectionTitle'))
        side.addWidget(label('Temukan domain yang tersedia,\nlalu periksa kualitasnya.', 'subtle', True))
        self.input_tabs = QTabWidget()
        manual = QWidget()
        manual.setObjectName('inputPage')
        manual_layout = QVBoxLayout(manual)
        manual_layout.setContentsMargins(0, 12, 0, 0)
        manual_layout.setSpacing(8)
        self.input = QPlainTextEdit()
        self.input.setObjectName('domainInput')
        self.input.setAccessibleName('Daftar domain, satu per baris')
        self.input.setPlaceholderText('example.com\nexample.net\nhttps://www.example.org')
        self.input.setMinimumHeight(140)
        self.input.setMaximumHeight(210)
        domain_label = label('Daftar domain', 'fieldLabel')
        domain_label.setBuddy(self.input)
        manual_layout.addWidget(domain_label)
        manual_layout.addWidget(self.input)
        manual_layout.addWidget(label('Maks. 1.000 domain unik per hunting.', 'hint', True))
        manual_layout.addWidget(button('Impor CSV / TXT', self.import_file, 'upload'))
        generator = QWidget()
        generator.setObjectName('inputPage')
        generator_layout = QVBoxLayout(generator)
        generator_layout.setContentsMargins(0, 12, 0, 0)
        generator_layout.setSpacing(8)
        self.keyword = QLineEdit()
        self.keyword.setPlaceholderText('kopi, kebun, wisata')
        self.keyword.setAccessibleName('Kata kunci generator domain')
        self.tlds = QLineEdit('com, net, org')
        self.tlds.setAccessibleName('Daftar TLD generator domain')
        for text, field in [('Kata kunci', self.keyword), ('Ekstensi / TLD', self.tlds)]:
            field_label = label(text, 'fieldLabel')
            field_label.setBuddy(field)
            generator_layout.addWidget(field_label)
            generator_layout.addWidget(field)
        generator_layout.addWidget(label('Pisahkan beberapa kata atau TLD dengan koma.', 'hint', True))
        generator_layout.addWidget(button('Buat daftar domain', self.generate, 'globe'))
        generator_layout.addStretch()
        self.input_tabs.addTab(manual, 'Tempel domain')
        self.input_tabs.addTab(generator, 'Generator')
        side.addWidget(self.input_tabs)
        self.import_note = label('URL dan IDN didukung. Duplikat dibuang otomatis.', 'hint', True)
        side.addWidget(self.import_note)
        self.start_button = button('Mulai hunting', self.new_job, 'play', primary=True)
        self.start_button.setMinimumHeight(44)
        side.addWidget(self.start_button)
        side.addWidget(label('Hunting baru masuk antrean jika ada proses aktif.', 'hint', True))
        side.addStretch(1)
        quota_card = QFrame()
        quota_card.setObjectName('quotaCard')
        quota_layout = QVBoxLayout(quota_card)
        quota_layout.setContentsMargins(14, 14, 14, 14)
        quota_layout.setSpacing(8)
        quota_layout.addWidget(label('KUOTA MOZ', 'eyebrow'))
        self.quota = label('', 'subtle', True)
        quota_layout.addWidget(self.quota)
        self.quota_progress = QProgressBar()
        self.quota_progress.setFixedHeight(6)
        self.quota_progress.setTextVisible(False)
        self.quota_progress.setAccessibleName('Pemakaian kuota Moz lokal')
        quota_layout.addWidget(self.quota_progress)
        quota_layout.addWidget(label('Batas lokal, bukan saldo akun Moz.', 'hint', True))
        side.addWidget(quota_card)
        sidebar_scroll.setWidget(sidebar)
        body.addWidget(sidebar_scroll)

        content = QVBoxLayout()
        content.setSpacing(16)
        self.content_layout = content
        title_row = QHBoxLayout()
        title_copy = QVBoxLayout()
        title_copy.setSpacing(4)
        title_copy.addWidget(label('Workspace domain', 'title'))
        self.heading_subtitle = label('Shortlist yang tersedia. Bukti pemeriksaan tetap bisa ditelusuri.', 'subtle', True)
        title_copy.addWidget(self.heading_subtitle)
        title_row.addLayout(title_copy, 1)
        self.export_button = button('Ekspor CSV', self.export, 'download')
        self.export_button.setToolTip('Ekspor domain available yang cocok dengan filter.')
        title_row.addWidget(self.export_button)
        content.addLayout(title_row)
        stats = QHBoxLayout()
        stats.setSpacing(12)
        self.stat_available = StatCard('Available', 'Kandidat untuk diperiksa', True)
        self.stat_checked = StatCard('Sudah diperiksa', 'Dari seluruh input')
        self.stat_valid = StatCard('Riwayat lolos', 'Valid atau tanpa riwayat')
        self.stat_registered = StatCard('Registered', 'Detail ada di log')
        for card in (self.stat_available, self.stat_checked, self.stat_valid, self.stat_registered):
            stats.addWidget(card, 1)
        content.addLayout(stats)

        job_card = QFrame()
        job_card.setObjectName('card')
        job_layout = QVBoxLayout(job_card)
        job_layout.setContentsMargins(16, 12, 16, 12)
        job_layout.setSpacing(8)
        job_row = QHBoxLayout()
        job_row.setSpacing(8)
        self.jobs = ComboBox()
        self.jobs.setAccessibleName('Pilih hunting tersimpan')
        self.jobs.setPlaceholderText('Belum ada hunting — mulai dari sidebar')
        self.jobs.setMinimumContentsLength(12)
        self.jobs.setSizePolicy(QSizePolicy.Policy.Expanding, QSizePolicy.Policy.Fixed)
        self.jobs.currentIndexChanged.connect(self.select_job)
        job_row.addWidget(self.jobs, 1)
        self.pause_button = button('Jeda', self.pause, 'pause')
        self.resume_button = button('Lanjutkan', self.resume, 'play')
        self.retry_button = button('Ulangi gagal', self.retry, 'refresh')
        self.retry_button.setToolTip('Coba ulang sumber gagal. Moz yang mungkin sudah ditagih tidak diulang otomatis.')
        for control in (self.pause_button, self.resume_button, self.retry_button):
            job_row.addWidget(control)
        job_layout.addLayout(job_row)
        self.progress_text = label('Belum ada hunting', 'fieldLabel')
        job_layout.addWidget(self.progress_text)
        self.progress = QProgressBar()
        self.progress.setFixedHeight(6)
        self.progress.setTextVisible(False)
        self.progress.setAccessibleName('Progres hunting')
        job_layout.addWidget(self.progress)
        self.stage_progress = label('', 'hint', True)
        job_layout.addWidget(self.stage_progress)
        content.addWidget(job_card)

        result_card = QFrame()
        result_card.setObjectName('card')
        result_layout = QVBoxLayout(result_card)
        result_layout.setContentsMargins(16, 8, 16, 12)
        result_layout.setSpacing(10)
        self.tabs = QTabWidget()
        self.results_page = QWidget()
        self.results_page.setObjectName('resultsPage')
        main_results = QVBoxLayout(self.results_page)
        main_results.setContentsMargins(0, 12, 0, 0)
        main_results.setSpacing(12)
        filter_row = QHBoxLayout()
        self.search = QLineEdit()
        self.search.setPlaceholderText('Cari domain available…')
        self.search.setAccessibleName('Cari domain dalam hasil available')
        self.search.textChanged.connect(self.render_results)
        filter_row.addWidget(self.search, 1)
        self.filter_button = button('Filter', lambda: None, 'filter')
        self.filter_button.setCheckable(True)
        filter_row.addWidget(self.filter_button)
        main_results.addLayout(filter_row)
        self.filter_panel = QDialog(self)
        self.filter_panel.setWindowTitle('Filter hasil available')
        self.filter_panel.resize(560, 260)
        filters = QGridLayout(self.filter_panel)
        filters.setContentsMargins(20, 20, 20, 20)
        filters.setHorizontalSpacing(10)
        filters.setVerticalSpacing(6)
        for col in range(3):
            filters.setColumnStretch(col, 1)
        filters.setRowStretch(5, 1)
        self.history_filter = ComboBox()
        self.history_filter.addItems(['Semua riwayat', 'valid', 'no_history', 'invalid', 'unchecked'])
        self.tld_filter = QLineEdit()
        self.tld_filter.setPlaceholderText('Contoh: co.id')
        self.min_opr = DoubleSpinBox()
        self.min_opr.setRange(0, 10)
        self.min_da = SpinBox()
        self.min_da.setRange(0, 100)
        self.max_len = SpinBox()
        self.max_len.setRange(0, 253)
        self.max_len.setSpecialValueText('Semua')
        self.min_snap = SpinBox()
        self.min_snap.setRange(0, 10000)
        fields = [('Riwayat', self.history_filter), ('TLD', self.tld_filter), ('OPR minimum', self.min_opr),
                  ('DA minimum', self.min_da), ('Panjang maks.', self.max_len), ('Snapshot min.', self.min_snap)]
        for i, (text, widget) in enumerate(fields):
            row, col = divmod(i, 3)
            caption = label(text, 'fieldLabel')
            caption.setBuddy(widget)
            widget.setAccessibleName(text)
            filters.addWidget(caption, row * 2, col)
            filters.addWidget(widget, row * 2 + 1, col)
            if isinstance(widget, QComboBox):
                widget.currentIndexChanged.connect(self.render_results)
            elif isinstance(widget, QLineEdit):
                widget.textChanged.connect(self.render_results)
            else:
                widget.valueChanged.connect(self.render_results)
        reset = button('Reset filter', self.reset_filters)
        filters.addWidget(reset, 4, 1)
        filters.addWidget(button('Selesai', self.filter_panel.accept), 4, 2)
        self.filter_panel.finished.connect(lambda: self.filter_button.setChecked(False))
        self.filter_panel.hide()
        self.filter_button.toggled.connect(self.filter_panel.setVisible)
        self.table = QTableWidget(0, len(self.columns))
        self.table.setHorizontalHeaderLabels([text for text, _ in self.columns])
        tips = {'opr': 'Open PageRank, skala 0–10; bukan Moz DA.',
                'da': 'Moz Domain Authority, skala 0–100.',
                'pa': 'Moz Page Authority, skala 0–100.',
                'spam': 'Moz Spam Score; kosong jika tidak tersedia.',
                'snapshots': 'HTML sukses dengan digest unik dari CDX, bukan seluruh tangkapan Wayback.'}
        for col, (_, key) in enumerate(self.columns):
            if key in tips:
                self.table.horizontalHeaderItem(col).setToolTip(tips[key])
        self.table.setAlternatingRowColors(True)
        self.table.setShowGrid(False)
        self.table.verticalHeader().hide()
        self.table.verticalHeader().setDefaultSectionSize(42)
        self.table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.table.setSelectionMode(QAbstractItemView.SelectionMode.SingleSelection)
        self.table.setAccessibleName('Hasil domain available')
        self.table.horizontalHeader().setSectionResizeMode(QHeaderView.ResizeMode.Interactive)
        self.table.horizontalHeader().setStretchLastSection(True)
        widths = {'domain': 200, 'status': 100, 'opr': 64, 'da': 72, 'pa': 72, 'spam': 86,
                  'history': 115, 'snapshots': 90}
        for col, (_, key) in enumerate(self.columns):
            if key in widths:
                self.table.setColumnWidth(col, widths[key])
            else:
                self.table.setColumnHidden(col, True)
        self.table.setSortingEnabled(True)
        self.table.itemSelectionChanged.connect(self.show_evidence)
        self.empty_panel = QWidget()
        empty_layout = QVBoxLayout(self.empty_panel)
        self.empty_layout = empty_layout
        self.empty_panel.setMinimumHeight(58)
        empty_layout.setContentsMargins(20, 24, 20, 24)
        empty_layout.setSpacing(10)
        empty_layout.addStretch()
        empty_icon = QLabel()
        self.empty_icon = empty_icon
        empty_icon.setPixmap(icon('search', COLORS['accent'], 44).pixmap(44, 44))
        empty_icon.setAlignment(Qt.AlignmentFlag.AlignCenter)
        empty_layout.addWidget(empty_icon)
        self.empty_results = label('Shortlist Anda dimulai di sini', 'sectionTitle', True)
        self.empty_results.setAlignment(Qt.AlignmentFlag.AlignCenter)
        empty_layout.addWidget(self.empty_results)
        self.empty_hint = label('Masukkan domain di sidebar untuk memulai hunting.', 'subtle', True)
        self.empty_hint.setAlignment(Qt.AlignmentFlag.AlignCenter)
        empty_layout.addWidget(self.empty_hint)
        empty_layout.addStretch()
        main_results.addWidget(self.empty_panel, 1)
        main_results.addWidget(self.table, 1)
        self.result_count = label('0 domain ditampilkan', 'hint')
        main_results.addWidget(self.result_count)
        self.log = QPlainTextEdit()
        self.log.setObjectName('log')
        self.log.setReadOnly(True)
        self.log.setAccessibleName('Log pemeriksaan seluruh domain dalam hunting')
        self.log.setPlaceholderText('Belum ada pemeriksaan. Status, bukti, dan kesalahan sumber akan tampil di sini.')
        self.tabs.addTab(self.results_page, 'Hasil available')
        self.tabs.addTab(self.log, 'Log pemeriksaan')
        result_layout.addWidget(self.tabs)
        self.details_toggle = QToolButton()
        self.details_toggle.setObjectName('disclosure')
        self.details_toggle.setText('Bukti pemeriksaan')
        self.details_toggle.setCheckable(True)
        self.details_toggle.setToolButtonStyle(Qt.ToolButtonStyle.ToolButtonTextBesideIcon)
        self.details_toggle.setArrowType(Qt.ArrowType.RightArrow)
        self.details_toggle.toggled.connect(self.update_details_visibility)
        result_layout.addWidget(self.details_toggle)
        self.details_panel = QDialog(self)
        self.details_panel.setWindowTitle('Bukti pemeriksaan domain')
        self.details_panel.resize(680, 430)
        self.details_panel.finished.connect(lambda: self.details_toggle.setChecked(False))
        detail_layout = QVBoxLayout(self.details_panel)
        detail_layout.setContentsMargins(20, 20, 20, 20)
        detail_layout.setSpacing(12)
        self.detail_title = label('Bukti pemeriksaan', 'sectionTitle', True)
        detail_layout.addWidget(self.detail_title)
        self.evidence = QPlainTextEdit()
        self.evidence.setObjectName('evidence')
        self.evidence.setReadOnly(True)
        self.evidence.setAccessibleName('Bukti RDAP DNS dan waktu metrik domain terpilih')
        detail_layout.addWidget(self.evidence)
        self.snapshot_link = button('Buka snapshot yang cocok', self.open_snapshot, 'arrow')
        self.snapshot_link.setEnabled(False)
        detail_layout.addWidget(self.snapshot_link)
        detail_layout.addWidget(button('Tutup', self.details_panel.accept))
        self.table.itemDoubleClicked.connect(lambda _: self.details_toggle.setChecked(True))
        self.details_panel.hide()
        self.tabs.currentChanged.connect(self.update_view_state)
        content.addWidget(result_card, 1)
        note = label('Available adalah kandidat, bukan jaminan bisa dibeli. Periksa ulang di registrar.\n'
                     'Riwayat memakai sampel arsip. Hasil ketersediaan basi setelah 15 menit.', 'hint', True)
        content.addWidget(note)
        body.addLayout(content, 1)
        layout.addLayout(body, 1)
        self.shortcuts = []
        for key, callback in [('Ctrl+Return', self.new_job), ('Ctrl+O', self.import_file),
                              ('Ctrl+F', self.focus_search), ('Ctrl+,', self.open_settings)]:
            shortcut = QShortcut(QKeySequence(key), self)
            shortcut.activated.connect(callback)
            self.shortcuts.append(shortcut)
        self.start_button.setToolTip('Mulai hunting atau tambahkan ke antrean · Ctrl+Enter')
        self.timer = QTimer(self)
        self.timer.timeout.connect(self.refresh)
        self.timer.start(1000)
        # Persisted running jobs are interrupted processes, not active workers.
        for job, _, state in self.store.jobs():
            if state == 'running':
                self.store.state(job, 'queued')
        self.refresh()
        if any(state == 'queued' for _, _, state in self.store.jobs()):
            QTimer.singleShot(0, self.start_engine)

    def resizeEvent(self, event):
        super().resizeEvent(event)
        if not hasattr(self, 'empty_layout'):
            return
        compact = event.size().height() < 800
        self.heading_subtitle.setVisible(not compact)
        for card in (self.stat_available, self.stat_checked, self.stat_valid, self.stat_registered):
            card.caption.setVisible(not compact)
        self.stage_progress.setVisible(not compact)
        self.empty_icon.setVisible(not compact)
        self.empty_hint.setVisible(not compact)
        self.empty_layout.setContentsMargins(12, 8 if compact else 24, 12, 8 if compact else 24)
        self.content_layout.setSpacing(12 if compact else 16)
        self.input.setMaximumHeight(150 if compact else 210)

    def focus_search(self):
        self.tabs.setCurrentIndex(0)
        self.search.setFocus()
        self.search.selectAll()

    def open_settings(self):
        if self.engine and self.engine.alive():
            QMessageBox.information(self, 'Hunting aktif', 'Jeda hunting dan tunggu worker berhenti sebelum mengganti pengaturan.')
            return
        if Settings(self.store, self).exec():
            self.refresh()

    def import_file(self):
        path, _ = QFileDialog.getOpenFileName(self, 'Impor domain', '', 'Daftar domain (*.csv *.txt)')
        if path:
            try:
                if Path(path).stat().st_size > 5_000_000:
                    raise ValueError('Berkas melebihi 5 MB')
                self.input.setPlainText(Path(path).read_text(encoding='utf-8-sig'))
            except (OSError, ValueError) as exc:
                QMessageBox.warning(self, 'Impor gagal', str(exc))

    def generate(self):
        words = [v.strip() for v in self.keyword.text().split(',') if v.strip()]
        tlds = [v.strip().lstrip('.') for v in self.tlds.text().split(',') if v.strip()]
        if len(words) * len(tlds) > 1000:
            QMessageBox.warning(self, 'Terlalu banyak', 'Maksimal 1.000 kombinasi.')
            return
        self.input.setPlainText('\n'.join(f'{word}.{tld}' for word in words for tld in tlds))
        self.input_tabs.setCurrentIndex(0)

    def new_job(self):
        try:
            parsed = parse_domains(self.input.toPlainText())
        except ValueError as exc:
            self.import_note.setText(str(exc))
            self.import_note.setStyleSheet('color: ' + COLORS['danger'])
            self.input_tabs.setCurrentIndex(0)
            self.input.setFocus()
            return
        self.import_note.setStyleSheet('')
        self.import_note.setText(f'{len(parsed.domains)} unik · {parsed.duplicates} duplikat dibuang · '
                                 f'{len(parsed.invalid)} tidak valid' +
                                 (': ' + ', '.join(parsed.invalid[:5]) if parsed.invalid else ''))
        if not parsed.domains:
            self.import_note.setText('Tambahkan minimal satu domain valid untuk memulai hunting.')
            self.input_tabs.setCurrentIndex(0)
            self.input.setFocus()
            return
        self.current = self.store.new_job(parsed.domains)
        self.refresh()
        self.start_engine()

    def start_engine(self):
        if self.engine and self.engine.alive():
            return
        if self.engine:
            self.engine.close()
        keys = {name: secrets.get(name) for name in secrets.NAMES}
        self.engine = Engine(self.store, keys)
        self.engine.start()

    def pause(self):
        if self.engine:
            self.engine.pause()
        if self.current:
            self.store.state(self.current, 'paused')
        self.statusBar().showMessage('Menjeda setelah permintaan aktif selesai…', 10000)

    def resume(self):
        if self.engine and self.engine.alive() and self.engine.stop.is_set():
            QMessageBox.information(self, 'Sedang menjeda', 'Tunggu permintaan aktif selesai, lalu lanjutkan.')
            return
        if self.current:
            state = next((s for j, _, s in self.store.jobs() if j == self.current), '')
            if state != 'completed':
                self.store.state(self.current, 'queued')
                self.start_engine()

    def retry(self):
        if not self.current:
            return
        if self.engine and self.engine.alive():
            QMessageBox.information(self, 'Hunting aktif', 'Jeda dan tunggu worker berhenti sebelum mencoba ulang.')
            return
        count = 0
        for result in self.store.results(self.current):
            if result.errors or result.status == 'unknown' or result.history == 'unchecked' or result.moz_state in ('no_key', 'quota_reserved'):
                result.stage = 'queued'
                self.store.save(self.current, result)
                count += 1
        if count:
            self.store.state(self.current, 'queued')
            self.start_engine()
        self.statusBar().showMessage('Sumber gagal dijadwalkan ulang. Moz gagal/terputus tidak ditagih ulang otomatis.', 10000)

    def select_job(self, index):
        self.current = self.jobs.itemData(index)
        self.render_results()

    def refresh(self):
        jobs = self.store.jobs()
        current = self.current
        self.jobs.blockSignals(True)
        self.jobs.clear()
        for job, created, state in reversed(jobs):
            self.jobs.addItem(f'{created[:16].replace("T", " ")} UTC  ·  {job[:7]}  ·  {state}', job)
        index = self.jobs.findData(current)
        self.jobs.setCurrentIndex(max(0, index))
        self.current = self.jobs.currentData()
        self.jobs.blockSignals(False)
        settings = self.store.settings()
        period = settings['moz_period'] or datetime.now(timezone.utc).strftime('%Y-%m')
        used = self.store.used(period)
        remaining = max(0, settings['moz_budget'] - settings['moz_reserve'] - used)
        self.quota.setText(f'{remaining} rows untuk finalis · {used} terpakai' +
                           f'\nCadangan {settings["moz_reserve"]} · periode {period}')
        self.quota_progress.setRange(0, max(1, settings['moz_budget']))
        self.quota_progress.setValue(min(used, settings['moz_budget']))
        if self.engine and self.engine.failure:
            self.statusBar().showMessage(self.engine.failure)
        self.render_results()

    def reset_filters(self):
        self.search.clear()
        self.history_filter.setCurrentIndex(0)
        self.tld_filter.clear()
        for field in (self.min_opr, self.min_da, self.max_len, self.min_snap):
            field.setValue(0)

    def update_details_visibility(self):
        expanded = self.details_toggle.isChecked()
        self.details_toggle.setArrowType(Qt.ArrowType.DownArrow if expanded else Qt.ArrowType.RightArrow)
        self.details_panel.setVisible(expanded and self.tabs.currentIndex() == 0)

    def update_view_state(self):
        on_results = self.tabs.currentIndex() == 0
        self.export_button.setEnabled(on_results and self.table.rowCount() > 0)
        self.details_toggle.setVisible(on_results and self.selected_result() is not None)
        self.update_details_visibility()

    def visible_results(self):
        results = self.store.results(self.current) if self.current else []
        tld = self.tld_filter.text().strip().lower().lstrip('.')
        return [r for r in results
                if r.status == 'available'
                and self.search.text().strip().casefold() in r.domain.casefold()
                and (self.history_filter.currentIndex() == 0 or r.history == self.history_filter.currentText())
                and (not tld or r.domain.endswith('.' + tld))
                and (self.min_opr.value() == 0 or (r.opr is not None and r.opr >= self.min_opr.value()))
                and (self.min_da.value() == 0 or (r.da is not None and r.da >= self.min_da.value()))
                and (self.max_len.value() == 0 or len(r.domain.split('.')[0]) <= self.max_len.value())
                and (self.min_snap.value() == 0 or (r.snapshots is not None and r.snapshots >= self.min_snap.value()))]

    def render_results(self):
        if not hasattr(self, 'table'):
            return
        selected = self.table.item(self.table.currentRow(), 0)
        selected_domain = selected.text() if selected else None
        all_results = self.store.results(self.current) if self.current else []
        done = sum(r.stage == 'done' for r in all_results)
        self.progress.setRange(0, max(1, len(all_results)))
        self.progress.setValue(done)
        self.progress.setFormat(f'{done} / {len(all_results)} domain selesai')
        checked = sum(bool(r.availability_at) or r.stage in ('history', 'moz_queued', 'done') for r in all_results)
        available = sum(r.status == 'available' for r in all_results)
        registered = sum(r.status == 'registered' for r in all_results)
        self.stage_progress.setText(f'Sudah diperiksa: {checked}  ·  Available: {available}  ·  Registered: {registered}  ·  '
                                    f'OPR: {sum(r.opr is not None for r in all_results)}  ·  '
                                    f'Riwayat: {sum(bool(r.history) for r in all_results)}  ·  '
                                    f'Moz: {sum(bool(r.moz_at) for r in all_results)}')
        valid = sum(r.status == 'available' and r.history in ('valid', 'no_history') for r in all_results)
        self.stat_available.set_value(available)
        self.stat_checked.set_value(checked)
        self.stat_valid.set_value(valid)
        self.stat_registered.set_value(registered)
        state = next((state for job, _, state in self.store.jobs() if job == self.current), '')
        state_label = {'queued': 'Dalam antrean', 'running': 'Sedang berjalan',
                       'paused': 'Dijeda', 'completed': 'Selesai'}.get(state, 'Belum ada hunting')
        self.progress_text.setText(f'{state_label}  ·  {done} / {len(all_results)} domain selesai' if all_results else state_label)
        active = bool(self.engine and self.engine.alive())
        self.start_button.setText('Tambahkan ke antrean' if active else 'Mulai hunting')
        self.pause_button.setEnabled(state in ('running', 'queued'))
        self.resume_button.setEnabled(state == 'paused' and not active)
        self.retry_button.setEnabled(state in ('completed', 'paused') and not active)
        lines = []
        for result in all_results:
            timestamp = result.availability_at or 'waktu belum tersedia'
            state = 'menunggu' if result.stage == 'queued' else result.status
            lines.append(f'[{timestamp}] {result.domain} | {state} | tahap={result.stage} | '
                         f'RDAP={result.rdap_http if result.rdap_http is not None else "—"} | '
                         f'DNS={result.dns or "—"} | RDAP URL={result.rdap_url or "—"}' +
                         (' | kesalahan=' + json.dumps(result.errors, ensure_ascii=False) if result.errors else ''))
        log_text = '\n'.join(lines)
        if self.log.toPlainText() != log_text:
            scrollbar = self.log.verticalScrollBar()
            at_end = scrollbar.value() == scrollbar.maximum()
            position = scrollbar.value()
            self.log.setPlainText(log_text)
            scrollbar.setValue(scrollbar.maximum() if at_end else position)
        rows = self.visible_results()
        self.tabs.setTabText(0, f'Hasil available ({available})')
        self.tabs.setTabText(1, f'Log pemeriksaan ({len(all_results)})')
        self.empty_results.setVisible(not rows)
        self.empty_panel.setVisible(not rows)
        self.table.setVisible(bool(rows))
        self.result_count.setText(f'{len(rows)} dari {available} domain available ditampilkan')
        if available:
            self.empty_results.setText('Tidak ada hasil yang cocok')
            self.empty_hint.setText('Ubah pencarian atau reset filter untuk melihat kembali kandidat.')
        elif all_results:
            self.empty_results.setText('Belum ada domain available')
            self.empty_hint.setText('Registered dan status lainnya ada di Log pemeriksaan. Coba daftar domain lain.')
        else:
            self.empty_results.setText('Shortlist Anda dimulai di sini')
            self.empty_hint.setText('Masukkan domain di sidebar, lalu mulai hunting. Hanya domain available yang tampil di sini.')
        filters_active = sum([self.history_filter.currentIndex() != 0, bool(self.tld_filter.text()),
                              self.min_opr.value() > 0, self.min_da.value() > 0,
                              self.max_len.value() > 0, self.min_snap.value() > 0])
        self.filter_button.setText(f'Filter ({filters_active})' if filters_active else 'Filter')
        signature = (self.current, json.dumps([r.to_dict() for r in rows], sort_keys=True))
        if getattr(self, '_render_signature', None) == signature:
            self.update_view_state()
            self.show_evidence()
            return
        self._render_signature = signature
        self.table.blockSignals(True)
        self.table.setSortingEnabled(False)
        self.table.setRowCount(len(rows))
        for i, result in enumerate(rows):
            for j, (_, key) in enumerate(self.columns):
                value = getattr(result, key)
                item = NumericItem('—' if value is None or value == '' else str(value))
                item.setData(Qt.ItemDataRole.UserRole, value)
                if key == 'status':
                    color = COLORS['accent']
                    item.setForeground(QColor(color))
                if key == 'history' and value == 'invalid':
                    item.setForeground(QColor(COLORS['danger']))
                if result.errors:
                    item.setToolTip(json.dumps(result.errors, ensure_ascii=False))
                self.table.setItem(i, j, item)
        self.table.setSortingEnabled(True)
        if selected_domain:
            for row in range(self.table.rowCount()):
                if self.table.item(row, 0).text() == selected_domain:
                    self.table.selectRow(row)
                    break
        self.table.blockSignals(False)
        self.update_view_state()
        self.show_evidence()

    def selected_result(self):
        item = self.table.item(self.table.currentRow(), 0)
        if not item or not self.current:
            return None
        return next((r for r in self.store.results(self.current) if r.domain == item.text()), None)

    def show_evidence(self):
        result = self.selected_result()
        self.snapshot_link.setEnabled(bool(result and result.matches))
        self.snapshot_link.setVisible(bool(result and result.matches))
        self.details_toggle.setEnabled(bool(result))
        if not result:
            self.evidence.clear()
            self._evidence_domain = None
            self.details_toggle.setChecked(False)
            return
        if self._evidence_domain != result.domain:
            self._evidence_domain = result.domain
            self.detail_title.setText(result.domain)
        proof = {'domain': result.domain, 'RDAP HTTP': result.rdap_http, 'RDAP URL': result.rdap_url,
                 'DNS': result.dns, 'availability_at': result.availability_at,
                 'registrar': result.registrar, 'expires': result.expires, 'stage': result.stage,
                 'ready_now': result.ready, 'OPR_at': result.opr_at, 'Wayback_at': result.history_at,
                 'sampled': result.sampled, 'matches': result.matches, 'Moz_at': result.moz_at, 'errors': result.errors}
        proof_text = json.dumps(proof, ensure_ascii=False, indent=2)
        if self.evidence.toPlainText() != proof_text:
            self.evidence.setPlainText(proof_text)
        self.update_view_state()

    def open_snapshot(self):
        result = self.selected_result()
        if result and result.matches:
            url = result.matches[0].get('snapshot', '')
            if url.startswith('https://web.archive.org/web/'):
                QDesktopServices.openUrl(QUrl(url))

    def export(self):
        if not self.current:
            return
        path, _ = QFileDialog.getSaveFileName(self, 'Ekspor hasil yang tampil', 'domains.csv', 'CSV (*.csv)')
        if path:
            try:
                export_csv(path, self.visible_results())
                self.statusBar().showMessage('CSV tersimpan; mencakup bukti dan waktu pemeriksaan.', 6000)
            except OSError as exc:
                QMessageBox.warning(self, 'Ekspor gagal', str(exc))

    def closeEvent(self, event):
        if self.engine and self.engine.alive():
            if self.closing_jobs is None:
                self.closing_jobs = [j for j, _, state in self.store.jobs() if state in ('running', 'queued')]
            self.engine.pause()
            self.statusBar().showMessage('Menutup setelah permintaan aktif selesai; progres disimpan lokal.')
            event.ignore()
            QTimer.singleShot(500, self.close)
            return
        if self.engine:
            self.engine.close()
        for job in self.closing_jobs or []:
            if next((s for j, _, s in self.store.jobs() if j == job), '') == 'paused':
                self.store.state(job, 'queued')
        self.store.close()
        event.accept()


def run():
    app = QApplication.instance() or QApplication([])
    apply_theme(app)
    lock = QLockFile(str(data_dir() / 'desktop.lock'))
    if not lock.tryLock(0):
        QMessageBox.warning(None, 'Aplikasi sudah terbuka', 'Gunakan jendela Moz Checker yang sudah berjalan untuk database ini.')
        return 1
    window = Window()
    window.show()
    return app.exec()
