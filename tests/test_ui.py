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
    assert window.table.rowCount() == 2
    window.status_filter.setCurrentText('available')
    assert window.table.rowCount() == 1
    window.table.selectRow(0)
    assert 'RDAP HTTP' in window.evidence.toPlainText()
    settings = Settings(store, window)
    qtbot.addWidget(settings)
    settings.budget.setValue(100)
    settings.reserve.setValue(10)
    settings.persist()
    assert store.settings()['moz_budget'] == 100
    window.close()
