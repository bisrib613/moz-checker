from moz_checker.models import Result
from moz_checker.ui import Window, Settings


def test_input_table_filter_and_settings(qtbot, store):
    window = Window(store)
    qtbot.addWidget(window)
    window.show()
    window.timer.stop()
    window.keyword.setText('kopi, kebun')
    window.tlds.setText('com, co.id')
    window.generate()
    assert window.input.toPlainText().splitlines() == ['kopi.com', 'kopi.co.id', 'kebun.com', 'kebun.co.id']
    job = store.new_job(['kopi.com', 'kebun.co.id'])
    store.save(job, Result('kopi.com', status='available', opr=6.2, stage='done'))
    store.save(job, Result('kebun.co.id', status='registered', opr=0, stage='done'))
    window.current = job
    window.refresh()
    assert window.table.rowCount() == 1
    assert window.table.item(0, 0).text() == 'kopi.com'
    assert 'kebun.co.id | registered' in window.log.toPlainText()
    assert 'Available: 1' in window.stage_progress.text()
    assert 'Registered: 1' in window.stage_progress.text()
    window.table.selectRow(0)
    assert 'RDAP HTTP' in window.evidence.toPlainText()
    settings = Settings(store, window)
    qtbot.addWidget(settings)
    settings.budget.setValue(100)
    settings.reserve.setValue(10)
    settings.persist()
    assert store.settings()['moz_budget'] == 100
    window.close()


def test_non_available_only_in_log_and_export(qtbot, store):
    window = Window(store)
    qtbot.addWidget(window)
    window.timer.stop()
    domains = ['google.com', 'redemption.com', 'pending.com', 'unknown.com', 'waiting.com']
    job = store.new_job(domains)
    for domain, status in zip(domains[:4], ['registered', 'redemption', 'pending delete', 'unknown']):
        store.save(job, Result(domain, status=status, stage='done', errors={'availability': 'timeout'} if status == 'unknown' else {}))
    store.state(job, 'completed')
    window.current = job
    window.refresh()
    assert window.table.rowCount() == 0
    assert window.visible_results() == []  # Export uses this exact selection.
    assert not window.empty_results.isHidden()
    assert 'google.com | registered' in window.log.toPlainText()
    assert 'redemption.com | redemption' in window.log.toPlainText()
    assert 'pending.com | pending delete' in window.log.toPlainText()
    assert 'unknown.com | unknown' in window.log.toPlainText()
    assert 'timeout' in window.log.toPlainText()
    assert 'waiting.com | menunggu' in window.log.toPlainText()
    assert len(window.log.toPlainText().splitlines()) == 5
    assert 'Sudah diperiksa: 4' in window.stage_progress.text()
    assert 'Available: 0' in window.stage_progress.text()
    window.close()


def test_search_filter_reset_and_export_selection(qtbot, store):
    window = Window(store)
    qtbot.addWidget(window)
    window.show()
    window.timer.stop()
    job = store.new_job(['alpha.com', 'beta.com', 'google.com'])
    store.save(job, Result('alpha.com', status='available', stage='done', opr=5.2, history='valid'))
    store.save(job, Result('beta.com', status='available', stage='done', opr=1.0, history='no_history'))
    store.save(job, Result('google.com', status='registered', stage='done'))
    store.state(job, 'completed')
    window.current = job
    window.refresh()
    assert window.stat_available.value.text() == '2'
    assert window.stat_valid.value.text() == '2'
    window.search.setText('BETA')
    assert [r.domain for r in window.visible_results()] == ['beta.com']
    window.filter_button.click()
    assert window.filter_panel.isVisible()
    window.min_opr.setValue(4)
    assert window.visible_results() == []
    assert not window.export_button.isEnabled()
    assert not window.empty_results.isHidden()
    window.reset_filters()
    assert [r.domain for r in window.visible_results()] == ['alpha.com', 'beta.com']
    assert window.export_button.isEnabled()
    window.filter_panel.accept()
    assert not window.filter_button.isChecked()
    window.close()


def test_evidence_on_demand_and_log_actions(qtbot, store):
    window = Window(store)
    qtbot.addWidget(window)
    window.show()
    window.timer.stop()
    job = store.new_job(['alpha.com', 'google.com'])
    store.save(job, Result('alpha.com', status='available', stage='done', registrar='Test Registrar', expires='2027-01-01'))
    store.save(job, Result('google.com', status='registered', stage='done'))
    store.state(job, 'completed')
    window.current = job
    window.refresh()
    window.table.selectRow(0)
    assert 'Test Registrar' in window.evidence.toPlainText()
    assert not window.details_panel.isVisible()
    window.details_toggle.click()
    assert window.details_panel.isVisible()
    window.refresh()
    assert window.selected_result().domain == 'alpha.com'
    assert window.details_panel.isVisible()
    window.tabs.setCurrentIndex(1)
    assert not window.details_panel.isVisible()
    assert not window.export_button.isEnabled()
    assert 'google.com | registered' in window.log.toPlainText()
    window.tabs.setCurrentIndex(0)
    window.details_panel.reject()
    assert not window.details_toggle.isChecked()
    window.close()


def test_compact_layout_and_keyboard_search(qtbot, store):
    from PySide6.QtCore import Qt
    from PySide6.QtWidgets import QApplication
    window = Window(store)
    qtbot.addWidget(window)
    window.resize(1040, 700)
    window.show()
    window.activateWindow()
    window.timer.stop()
    qtbot.waitUntil(window.isActiveWindow)
    QApplication.processEvents()
    assert window.size().width() == 1040
    assert window.empty_results.isVisible()
    assert window.empty_results.height() > 0
    assert window.centralWidget().rect().contains(window.start_button.mapTo(window.centralWidget(), window.start_button.rect().bottomRight()))
    window.input.setFocus()
    qtbot.keyClick(window.input, Qt.Key.Key_F, Qt.KeyboardModifier.ControlModifier)
    assert window.search.hasFocus()
    window.close()


def test_empty_input_has_inline_feedback_without_job(qtbot, store):
    window = Window(store)
    qtbot.addWidget(window)
    window.timer.stop()
    window.new_job()
    assert store.jobs() == []
    assert 'minimal satu domain valid' in window.import_note.text()
    window.input.setPlainText('\n'.join(f'test{i}.com' for i in range(1001)))
    window.new_job()
    assert store.jobs() == []
    assert '1,000' in window.import_note.text()
    assert 'test1000.com' in window.input.toPlainText()
    window.close()
