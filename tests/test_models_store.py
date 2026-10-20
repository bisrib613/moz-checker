import csv
from datetime import datetime, timedelta, timezone

import pytest

from moz_checker.models import Result, fresh, normalize, now, parse_domains
from moz_checker.store import export_csv


@pytest.mark.parametrize(('raw', 'expected'), [
    ('HTTPS://WWW.Example.COM/path?q=1', 'example.com'), ('bücher.de', 'xn--bcher-kva.de'),
    ('a.co.id.', 'a.co.id'), ('www.example.org', 'example.org'),
])
def test_normalize(raw, expected):
    assert normalize(raw) == expected


@pytest.mark.parametrize('raw', ['localhost', '127.0.0.1', 'a..com', '-a.com', 'a.com:80',
                                      'https://user:pass@example.com', 'a.123', '', 'blog.example.com', 'co.id'])
def test_invalid_domains(raw):
    with pytest.raises(ValueError):
        normalize(raw)


def test_csv_reimport_and_limit():
    parsed = parse_domains('domain,status\nEXAMPLE.com,available\nwww.example.com,registered\nbücher.de,unknown\nno,unknown')
    assert parsed.domains == ['example.com', 'xn--bcher-kva.de']
    assert parsed.duplicates == 1
    assert parsed.invalid == ['no']
    with pytest.raises(ValueError, match='1,000'):
        parse_domains('\n'.join(f'd{i}.com' for i in range(1001)))


def test_job_resume_persistence(store):
    job = store.new_job(['a.com', 'b.com'])
    result = store.results(job)[0]
    result.stage, result.status = 'done', 'registered'
    store.save(job, result)
    store.state(job, 'paused')
    assert [r.stage for r in store.results(job)] == ['done', 'queued']
    assert store.jobs()[0][2] == 'paused'


def test_cache_and_quota_reservation(store):
    store.cache_put('opr', 'a.com', {'opr': 0})
    assert store.cache_get('opr', 'a.com', 3600) == {'opr': 0}
    assert store.cache_get('opr', 'a.com', 0) is None
    assert store.reserve_row('billing-1', 2, 1)
    assert not store.reserve_row('billing-1', 2, 1)
    assert store.used('billing-1') == 1
    assert store.used('billing-2') == 0


def test_freshness_and_ready():
    r = Result('a.com', status='available', history='valid', availability_at=now())
    assert r.ready
    r.availability_at = (datetime.now(timezone.utc) - timedelta(minutes=16)).isoformat()
    assert not r.ready
    assert not fresh('broken', 900)


def test_csv_provenance_null_and_formula_safety(store, tmp_path):
    r = Result('a.com', registrar='=HYPERLINK("bad")', opr=0, errors={'history': 'HTTP 503'})
    export_csv(tmp_path / 'out.csv', [r])
    with open(tmp_path / 'out.csv', encoding='utf-8-sig', newline='') as handle:
        row = next(csv.DictReader(handle))
    assert row['da'] == ''
    assert row['opr'] == '0'
    assert row['registrar'].startswith("'=")
    assert 'OPR' in row['authority_sources']
    assert '503' in row['errors']


def test_quota_limit_shared_between_database_connections(store):
    from pathlib import Path
    from moz_checker.store import Store
    path = Path(store.db.execute('PRAGMA database_list').fetchone()[2])
    second = Store(path)
    assert store.reserve_row('shared', 2, 1)
    assert not second.reserve_row('shared', 2, 1)
    assert second.used('shared') == 1
    second.close()
