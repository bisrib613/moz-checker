from datetime import datetime, timezone
import json
from pathlib import Path

from PySide6.QtCore import Qt, QTimer, QUrl, QLockFile
from PySide6.QtGui import QColor, QDesktopServices
from PySide6.QtWidgets import (
    QAbstractItemView, QApplication, QCheckBox, QComboBox, QDialog, QDialogButtonBox,
    QDoubleSpinBox, QFileDialog, QFormLayout, QGridLayout, QHBoxLayout, QHeaderView,
    QLabel, QLineEdit, QMainWindow, QMessageBox, QPlainTextEdit, QProgressBar,
    QPushButton, QSpinBox, QSplitter, QTableWidget, QTableWidgetItem, QVBoxLayout, QWidget,
)

from . import secrets
from .engine import Engine
from .models import parse_domains
from .store import Store, data_dir, export_csv

STYLE = '''
QMainWindow, QDialog { background: #10151d; color: #e3e8ef; }
QWidget { color: #e3e8ef; font-family: "Segoe UI", sans-serif; font-size: 12px; }
QLineEdit, QPlainTextEdit, QComboBox, QSpinBox, QDoubleSpinBox {
 background: #1a2330; border: 1px solid #344154; border-radius: 5px; padding: 6px; }
QPushButton { background: #263448; border: 1px solid #3c4e66; padding: 8px 14px; border-radius: 6px; }
QPushButton:hover { background: #354863; }
QPushButton:disabled { color: #748196; }
QPushButton#primary { background: #226c50; border-color: #389c76; font-weight: bold; }
QTableWidget { background: #151e29; alternate-background-color: #192432; gridline-color: #293648; }
QHeaderView::section { background: #202e40; padding: 8px; border: none; }
QProgressBar { background: #1a2330; border: 1px solid #344154; border-radius: 4px; text-align: center; height: 18px; }
QProgressBar::chunk { background: #288465; }
QLabel#title { font-size: 25px; font-weight: 700; }
QLabel#subtle { color: #9aabc0; }
QCheckBox { spacing: 8px; }
'''


class Settings(QDialog):
    def __init__(self, store, parent=None):
        super().__init__(parent)
        self.store = store
        self.setWindowTitle('Pengaturan • tersimpan di perangkat ini')
        self.resize(650, 650)
        layout = QVBoxLayout(self)
        info = QLabel('Moz memakai Links API v2 (Access ID + Secret), bukan token API v3.\n'
                      'Kuota berikut adalah batas lokal; bukan saldo akun Moz. Verifikasi paket Anda.\n'
                      'Biarkan key kosong untuk mempertahankan key yang tersimpan.')
        info.setWordWrap(True)
        layout.addWidget(info)
        form = QFormLayout()
        self.keys = {}
        self.deletes = {}
        for name, label in [('opr_key', 'Open PageRank key'), ('moz_id', 'Moz Access ID'), ('moz_secret', 'Moz Secret')]:
            field = QLineEdit()
            field.setEchoMode(QLineEdit.EchoMode.Password)
            field.setPlaceholderText('Tersimpan' if secrets.get(name) else 'Belum diatur')
            delete = QCheckBox('Hapus key tersimpan')
            box = QWidget()
            box_layout = QVBoxLayout(box)
            box_layout.setContentsMargins(0, 0, 0, 0)
            box_layout.addWidget(field)
            box_layout.addWidget(delete)
            form.addRow(label, box)
            self.keys[name], self.deletes[name] = field, delete
        settings = store.settings()
        self.budget = QSpinBox()
        self.budget.setRange(0, 10000000)
        self.budget.setValue(settings['moz_budget'])
        self.reserve = QSpinBox()
        self.reserve.setRange(0, 10000000)
        self.reserve.setValue(settings['moz_reserve'])
        self.samples = QSpinBox()
        self.samples.setRange(1, 25)
        self.samples.setValue(settings['samples'])
        self.opr = QDoubleSpinBox()
        self.opr.setRange(0, 10)
        self.opr.setValue(settings['opr_min'])
        self.period = QLineEdit(settings['moz_period'])
        self.period.setPlaceholderText('Kosong = bulan UTC berjalan, atau ID periode tagihan Anda')
        self.empty = QCheckBox('Domain tanpa snapshot dianggap valid (tanpa riwayat)')
        self.empty.setChecked(settings['empty_valid'])
        self.keywords = QPlainTextEdit('\n'.join(settings['keywords']))
        self.keywords.setMaximumHeight(110)
        for label, field in [('Batas rows Moz / periode', self.budget), ('Cadangan rows', self.reserve),
                             ('ID periode tagihan', self.period), ('Sampel snapshot', self.samples),
                             ('OPR minimum untuk tahap arsip', self.opr), ('Tanpa riwayat', self.empty),
                             ('Kata kunci (satu per baris)', self.keywords)]:
            form.addRow(label, field)
        layout.addLayout(form)
        buttons = QDialogButtonBox(QDialogButtonBox.StandardButton.Save | QDialogButtonBox.StandardButton.Cancel)
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
    columns = [('Domain', 'domain'), ('Status', 'status'), ('Riwayat', 'history'), ('Tahap', 'stage'),
               ('Kedaluwarsa', 'expires'), ('Registrar', 'registrar'), ('OPR', 'opr'), ('Moz DA', 'da'),
               ('Moz PA', 'pa'), ('Moz Spam', 'spam'), ('Snapshot*', 'snapshots'), ('Moz', 'moz_state')]

    def __init__(self, store=None):
        super().__init__()
        self.store = store or Store(data_dir() / 'checker.sqlite3')
        self.engine = None
        self.closing_jobs = None
        self.current = None
        self.setWindowTitle('Moz Checker • Expired Domain Research')
        self.resize(1380, 900)
        root = QWidget()
        self.setCentralWidget(root)
        layout = QVBoxLayout(root)
        layout.setContentsMargins(24, 22, 24, 16)
        heading = QHBoxLayout()
        title = QLabel('Expired Domain Checker')
        title.setObjectName('title')
        heading.addWidget(title)
        heading.addStretch()
        settings = QPushButton('Pengaturan & API key')
        settings.clicked.connect(self.open_settings)
        heading.addWidget(settings)
        layout.addLayout(heading)
        subtitle = QLabel('LOCAL RESEARCH WORKSPACE    /    RDAP → DNS → OPR → WAYBACK → MOZ')
        subtitle.setObjectName('subtle')
        layout.addWidget(subtitle)
        self.quota = QLabel()
        layout.addWidget(self.quota)
        splitter = QSplitter(Qt.Orientation.Vertical)
        input_area = QWidget()
        input_layout = QVBoxLayout(input_area)
        input_layout.setContentsMargins(0, 8, 0, 0)
        self.input = QPlainTextEdit()
        self.input.setPlaceholderText('Tempel hingga 1.000 domain atau URL, satu per baris…\nexample.com\nhttps://www.example.org')
        self.input.setMaximumHeight(130)
        input_layout.addWidget(self.input)
        controls = QHBoxLayout()
        upload = QPushButton('Impor CSV / TXT')
        upload.clicked.connect(self.import_file)
        controls.addWidget(upload)
        self.keyword = QLineEdit()
        self.keyword.setPlaceholderText('Kata kunci: kebun, kopi')
        controls.addWidget(self.keyword)
        self.tlds = QLineEdit('com, net, org')
        self.tlds.setMaximumWidth(190)
        controls.addWidget(self.tlds)
        generate = QPushButton('Generate')
        generate.clicked.connect(self.generate)
        controls.addWidget(generate)
        start = QPushButton('Mulai hunting')
        start.setObjectName('primary')
        start.clicked.connect(self.new_job)
        controls.addWidget(start)
        input_layout.addLayout(controls)
        self.import_note = QLabel('Belum ada domain dimasukkan.')
        self.import_note.setObjectName('subtle')
        input_layout.addWidget(self.import_note)
        splitter.addWidget(input_area)
        results_area = QWidget()
        results_layout = QVBoxLayout(results_area)
        results_layout.setContentsMargins(0, 8, 0, 0)
        jobs = QHBoxLayout()
        self.jobs = QComboBox()
        self.jobs.currentIndexChanged.connect(self.select_job)
        jobs.addWidget(self.jobs, 1)
        self.pause_button = QPushButton('Jeda')
        self.pause_button.clicked.connect(self.pause)
        jobs.addWidget(self.pause_button)
        resume = QPushButton('Lanjutkan')
        resume.clicked.connect(self.resume)
        jobs.addWidget(resume)
        retry = QPushButton('Coba ulang sumber gagal')
        retry.clicked.connect(self.retry)
        jobs.addWidget(retry)
        export = QPushButton('Ekspor CSV')
        export.clicked.connect(self.export)
        jobs.addWidget(export)
        results_layout.addLayout(jobs)
        self.progress = QProgressBar()
        results_layout.addWidget(self.progress)
        self.stage_progress = QLabel()
        results_layout.addWidget(self.stage_progress)
        filters = QGridLayout()
        self.status_filter = QComboBox()
        self.status_filter.addItems(['Semua status', 'available', 'registered', 'redemption', 'pending delete', 'unknown'])
        self.history_filter = QComboBox()
        self.history_filter.addItems(['Semua riwayat', 'valid', 'no_history', 'invalid', 'unchecked'])
        self.tld_filter = QLineEdit()
        self.tld_filter.setPlaceholderText('TLD: co.id')
        self.min_opr = QDoubleSpinBox()
        self.min_opr.setRange(0, 10)
        self.min_opr.setPrefix('OPR ≥ ')
        self.min_da = QSpinBox()
        self.min_da.setRange(0, 100)
        self.min_da.setPrefix('DA ≥ ')
        self.max_len = QSpinBox()
        self.max_len.setRange(0, 253)
        self.max_len.setSpecialValueText('Panjang: semua')
        self.max_len.setPrefix('Panjang ≤ ')
        self.min_snap = QSpinBox()
        self.min_snap.setRange(0, 10000)
        self.min_snap.setPrefix('Snapshot ≥ ')
        for col, widget in enumerate([self.status_filter, self.history_filter, self.tld_filter,
                                      self.min_opr, self.min_da, self.max_len, self.min_snap]):
            filters.addWidget(widget, 0, col)
            if isinstance(widget, QComboBox):
                widget.currentIndexChanged.connect(self.render_results)
            elif isinstance(widget, QLineEdit):
                widget.textChanged.connect(self.render_results)
            else:
                widget.valueChanged.connect(self.render_results)
        results_layout.addLayout(filters)
        self.table = QTableWidget(0, len(self.columns))
        self.table.setHorizontalHeaderLabels([label for label, _ in self.columns])
        self.table.setAlternatingRowColors(True)
        self.table.setEditTriggers(QAbstractItemView.EditTrigger.NoEditTriggers)
        self.table.setSelectionBehavior(QAbstractItemView.SelectionBehavior.SelectRows)
        self.table.horizontalHeader().setSectionResizeMode(QHeaderView.ResizeMode.ResizeToContents)
        self.table.horizontalHeader().setStretchLastSection(True)
        self.table.setSortingEnabled(True)
        self.table.itemSelectionChanged.connect(self.show_evidence)
        results_layout.addWidget(self.table)
        self.evidence = QPlainTextEdit()
        self.evidence.setReadOnly(True)
        self.evidence.setMaximumHeight(145)
        self.evidence.setPlaceholderText('Pilih baris untuk melihat bukti RDAP/DNS, waktu metrik, dan kesalahan sumber.')
        results_layout.addWidget(self.evidence)
        self.snapshot_link = QPushButton('Buka snapshot yang cocok di browser')
        self.snapshot_link.setEnabled(False)
        self.snapshot_link.clicked.connect(self.open_snapshot)
        results_layout.addWidget(self.snapshot_link)
        splitter.addWidget(results_area)
        splitter.setStretchFactor(1, 1)
        layout.addWidget(splitter)
        footer = QLabel('Ketersediaan dapat berubah; hasil >15 menit dianggap basi. Periksa ulang di registrar sebelum membeli.\n'
                        'Hormati merek dagang. Arsip dinilai dari sampel, bukan jaminan bersih. *Snapshot = HTML sukses dengan digest unik.')
        footer.setObjectName('subtle')
        layout.addWidget(footer)
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

    def new_job(self):
        try:
            parsed = parse_domains(self.input.toPlainText())
        except ValueError as exc:
            QMessageBox.warning(self, 'Input', str(exc))
            return
        self.import_note.setText(f'{len(parsed.domains)} unik · {parsed.duplicates} duplikat dibuang · '
                                 f'{len(parsed.invalid)} tidak valid' +
                                 (': ' + ', '.join(parsed.invalid[:5]) if parsed.invalid else ''))
        if not parsed.domains:
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
        self.quota.setText(f'Moz · periode {period} · terpakai lokal {used} / {settings["moz_budget"]} rows '
                           f'· cadangan {settings["moz_reserve"]} · tersedia untuk antrean '
                           f'{max(0, settings["moz_budget"] - settings["moz_reserve"] - used)}')
        if self.engine and self.engine.failure:
            self.statusBar().showMessage(self.engine.failure)
        self.render_results()

    def visible_results(self):
        results = self.store.results(self.current) if self.current else []
        tld = self.tld_filter.text().strip().lower().lstrip('.')
        return [r for r in results
                if (self.status_filter.currentIndex() == 0 or r.status == self.status_filter.currentText())
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
        self.stage_progress.setText(f'Ketersediaan: {sum(bool(r.availability_at) for r in all_results)}  ·  '
                                    f'OPR: {sum(r.opr is not None for r in all_results)}  ·  '
                                    f'Riwayat: {sum(bool(r.history) for r in all_results)}  ·  '
                                    f'Moz: {sum(bool(r.moz_at) for r in all_results)}')
        rows = self.visible_results()
        self.table.blockSignals(True)
        self.table.setSortingEnabled(False)
        self.table.setRowCount(len(rows))
        for i, result in enumerate(rows):
            for j, (_, key) in enumerate(self.columns):
                value = getattr(result, key)
                item = NumericItem('—' if value is None or value == '' else str(value))
                item.setData(Qt.ItemDataRole.UserRole, value)
                if key == 'status':
                    color = '#73dbb0' if result.status == 'available' else '#e9bf79' if result.status == 'unknown' else '#9aabc0'
                    item.setForeground(QColor(color))
                if key == 'history' and value == 'invalid':
                    item.setForeground(QColor('#ff8989'))
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
        self.show_evidence()

    def selected_result(self):
        item = self.table.item(self.table.currentRow(), 0)
        if not item or not self.current:
            return None
        return next((r for r in self.store.results(self.current) if r.domain == item.text()), None)

    def show_evidence(self):
        result = self.selected_result()
        self.snapshot_link.setEnabled(bool(result and result.matches))
        if not result:
            self.evidence.clear()
            return
        proof = {'domain': result.domain, 'RDAP HTTP': result.rdap_http, 'RDAP URL': result.rdap_url,
                 'DNS': result.dns, 'availability_at': result.availability_at,
                 'ready_now': result.ready, 'OPR_at': result.opr_at, 'Wayback_at': result.history_at,
                 'sampled': result.sampled, 'matches': result.matches, 'Moz_at': result.moz_at, 'errors': result.errors}
        self.evidence.setPlainText(json.dumps(proof, ensure_ascii=False, indent=2))

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
    app.setStyle('Fusion')
    app.setStyleSheet(STYLE)
    lock = QLockFile(str(data_dir() / 'desktop.lock'))
    if not lock.tryLock(0):
        QMessageBox.warning(None, 'Aplikasi sudah terbuka', 'Gunakan jendela Moz Checker yang sudah berjalan untuk database ini.')
        return 1
    window = Window()
    window.show()
    return app.exec()
