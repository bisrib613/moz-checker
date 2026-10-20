import csv
import json
import os
from pathlib import Path
import sqlite3
import threading
import uuid

from .models import Result, fresh, now


def data_dir() -> Path:
    override = os.environ.get('MOZ_CHECKER_DATA_DIR')
    root = Path(override) if override else Path(os.environ.get('LOCALAPPDATA', Path.home() / '.local/share')) / 'MozChecker'
    root.mkdir(parents=True, exist_ok=True)
    return root


DEFAULTS = {
    'moz_budget': 50, 'moz_reserve': 5, 'moz_period': '', 'samples': 5,
    'empty_valid': True, 'opr_min': 0.0,
    'keywords': ['judi', 'slot gacor', 'casino', 'togel', 'sportsbook', 'porn', 'xxx', 'bokep', 'pornography'],
}


class Store:
    def __init__(self, path: Path):
        self.lock = threading.RLock()
        self.db = sqlite3.connect(path, check_same_thread=False)
        self.db.execute('PRAGMA journal_mode=WAL')
        self.db.executescript('''
        CREATE TABLE IF NOT EXISTS jobs(id TEXT PRIMARY KEY, created TEXT, state TEXT);
        CREATE TABLE IF NOT EXISTS results(job TEXT, domain TEXT, ordinal INTEGER, data TEXT,
          PRIMARY KEY(job, domain));
        CREATE TABLE IF NOT EXISTS cache(source TEXT, domain TEXT, checked TEXT, data TEXT,
          PRIMARY KEY(source,domain));
        CREATE TABLE IF NOT EXISTS settings(key TEXT PRIMARY KEY, value TEXT);
        CREATE TABLE IF NOT EXISTS quota(period TEXT PRIMARY KEY, used INTEGER NOT NULL);
        ''')
        self.db.commit()

    def settings(self):
        with self.lock:
            values = dict(DEFAULTS)
            values.update({k: json.loads(v) for k, v in self.db.execute('SELECT key,value FROM settings')})
            return values

    def configure(self, values):
        with self.lock, self.db:
            self.db.executemany('INSERT OR REPLACE INTO settings VALUES (?,?)',
                                [(k, json.dumps(v)) for k, v in values.items() if k in DEFAULTS])

    def new_job(self, domains):
        job = uuid.uuid4().hex
        with self.lock, self.db:
            self.db.execute('INSERT INTO jobs VALUES (?,?,?)', (job, now(), 'queued'))
            self.db.executemany('INSERT INTO results VALUES (?,?,?,?)',
                                [(job, d, i, json.dumps(Result(d).to_dict())) for i, d in enumerate(domains)])
        return job

    def jobs(self):
        with self.lock:
            return list(self.db.execute('SELECT id,created,state FROM jobs ORDER BY created'))

    def state(self, job, state):
        with self.lock, self.db:
            self.db.execute('UPDATE jobs SET state=? WHERE id=?', (state, job))

    def results(self, job):
        with self.lock:
            return [Result(**json.loads(r[0])) for r in self.db.execute(
                'SELECT data FROM results WHERE job=? ORDER BY ordinal', (job,))]

    def save(self, job, result):
        with self.lock, self.db:
            self.db.execute('UPDATE results SET data=? WHERE job=? AND domain=?',
                            (json.dumps(result.to_dict()), job, result.domain))

    def cache_get(self, source, domain, ttl):
        with self.lock:
            row = self.db.execute('SELECT checked,data FROM cache WHERE source=? AND domain=?',
                                  (source, domain)).fetchone()
            return json.loads(row[1]) if row and fresh(row[0], ttl) else None

    def cache_put(self, source, domain, value):
        with self.lock, self.db:
            self.db.execute('INSERT OR REPLACE INTO cache VALUES (?,?,?,?)',
                            (source, domain, now(), json.dumps(value)))

    def used(self, period):
        with self.lock:
            row = self.db.execute('SELECT used FROM quota WHERE period=?', (period,)).fetchone()
            return row[0] if row else 0

    def reserve_row(self, period, budget, reserve):
        # Reserve BEFORE network I/O: failures may still be billable. Never refund automatically.
        with self.lock, self.db:
            self.db.execute('BEGIN IMMEDIATE')
            if self.used(period) >= max(0, budget - reserve):
                return False
            self.db.execute('INSERT INTO quota VALUES (?,1) ON CONFLICT(period) DO UPDATE SET used=used+1',
                            (period,))
            return True

    def close(self):
        self.db.close()


def export_csv(path, results):
    fields = list(Result('').to_dict()) + ['ready', 'authority_sources']
    with open(path, 'w', newline='', encoding='utf-8-sig') as handle:
        writer = csv.DictWriter(handle, fieldnames=fields)
        writer.writeheader()
        for result in results:
            row = result.to_dict() | {'ready': result.ready, 'authority_sources': 'OPR; Moz DA; Moz PA; Moz Spam Score'}
            for k, value in row.items():
                if isinstance(value, (dict, list)):
                    row[k] = json.dumps(value, ensure_ascii=False)
                if isinstance(row[k], str) and row[k].startswith(('=', '+', '-', '@', '\t', '\r')):
                    row[k] = "'" + row[k]
            writer.writerow(row)
