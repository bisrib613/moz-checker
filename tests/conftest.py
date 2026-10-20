import os
os.environ.setdefault('QT_QPA_PLATFORM', 'offscreen')

import pytest
from moz_checker.store import Store


@pytest.fixture
def store(tmp_path):
    db = Store(tmp_path / 'test.sqlite3')
    yield db
    db.close()
