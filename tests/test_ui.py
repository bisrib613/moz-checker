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
