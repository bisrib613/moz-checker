from concurrent.futures import ThreadPoolExecutor, as_completed
from datetime import datetime, timezone
import threading

from .models import fresh
from .providers import Providers, Cancelled, SourceError


def update(result, values):
    for key, value in values.items():
        if hasattr(result, key):
            setattr(result, key, value)


class Engine:
    """One hunting at a time, bounded availability workers, independent serial history/Moz lanes."""
    def __init__(self, store, keys, notify=lambda: None, provider_factory=Providers):
        self.store, self.notify = store, notify
        self.stop = threading.Event()
        self.providers = provider_factory(store, self.stop, keys)
        self.keys = keys
        self.thread = None
        self.failure = ''

    def start(self):
        if self.thread and self.thread.is_alive():
            return
        self.stop.clear()
        self.failure = ''
        self.thread = threading.Thread(target=self.run, name='hunting-coordinator', daemon=True)
        self.thread.start()

    def pause(self):
        self.stop.set()

    def alive(self):
        return bool(self.thread and self.thread.is_alive())

    def emit(self, job, result):
        self.store.save(job, result)
        self.notify()

    def run(self):
        try:
            while not self.stop.is_set():
                jobs = [j for j in self.store.jobs() if j[2] in ('queued', 'running')]
                if not jobs:
                    break
                job = jobs[0][0]
                self.store.state(job, 'running')
                self.notify()
                try:
                    self.run_job(job)
                except Cancelled:
                    self.store.state(job, 'paused')
                    break
                except Exception as exc:
                    # Persist a useful type only; third-party exception strings can contain headers.
                    self.failure = f'Worker berhenti: {type(exc).__name__}. Hunting dijeda; coba lanjutkan.'
                    self.store.state(job, 'paused')
                    break
                if self.stop.is_set():
                    self.store.state(job, 'paused')
                    break
                self.store.state(job, 'completed')
                self.notify()
        finally:
            self.notify()

    def run_job(self, job):
        settings = self.store.settings()
        results = [r for r in self.store.results(job) if r.stage != 'done']
        # Credentials are loaded once into memory by the UI. Never stored in SQLite.
        with ThreadPoolExecutor(max_workers=8) as availability, \
                ThreadPoolExecutor(max_workers=1) as archives, ThreadPoolExecutor(max_workers=1) as moz:
            history_futures, moz_futures = [], []
            # Cheap availability first. Batch only available candidates for OPR.
            candidates = []

            def enrich(batch):
                if self.stop.is_set():
                    raise Cancelled()
                try:
                    scores = self.providers.opr([r.domain for r in batch])
                    for result in batch:
                        if result.domain in scores:
                            update(result, scores[result.domain])
                            result.errors.pop('opr', None)
                        elif self.keys.get('opr_key'):
                            result.errors['opr'] = 'Tidak ada data OPR'
                except SourceError as exc:
                    for result in batch:
                        result.errors['opr'] = str(exc)
                for result in batch:
                    self.emit(job, result)
                    if result.opr is None or result.opr >= settings['opr_min']:
                        history_futures.append(archives.submit(
                            self.check_history, job, result, settings, moz, moz_futures))
                    else:
                        result.stage = 'done'
                        self.emit(job, result)

            futures = {availability.submit(self.check_availability, job, r): r for r in results}
            for future in as_completed(futures):
                result = future.result()
                if result.status == 'available':
                    candidates.append(result)
                    if len(candidates) == 100 or not self.keys.get('opr_key'):
                        enrich(candidates)
                        candidates = []
                else:
                    result.stage = 'done'
                    self.emit(job, result)
            if candidates:
                enrich(candidates)
            for future in history_futures:
                future.result()
            for future in moz_futures:
                future.result()

    def check_availability(self, job, result):
        if self.stop.is_set():
            raise Cancelled()
        result.stage = 'availability'
        self.emit(job, result)
        try:
            proof = self.providers.availability(result.domain)
            update(result, proof)
            result.errors.pop('availability', None)
            if proof.get('reason'):
                result.errors['availability'] = proof['reason']
        except SourceError as exc:
            result.status = 'unknown'
            result.errors['availability'] = str(exc)
        self.emit(job, result)
        return result

    def check_history(self, job, result, settings, moz, moz_futures):
        if self.stop.is_set():
            raise Cancelled()
        result.stage = 'history'
        self.emit(job, result)
        try:
            # Re-scan on retry so keyword/settings changes cannot reuse an old verdict.
            update(result, self.providers.history(result.domain, settings['samples'],
                                                  settings['keywords'], settings['empty_valid']))
            result.errors.pop('history', None)
            if result.history == 'unchecked':
                result.errors['history'] = 'Sampel tidak lengkap atau kebijakan tanpa riwayat tidak mengizinkan valid'
        except SourceError as exc:
            result.history = 'unchecked'
            result.errors['history'] = str(exc)
        if result.history in ('valid', 'no_history') and not result.moz_at:
            result.stage = 'moz_queued'
            self.emit(job, result)
            moz_futures.append(moz.submit(self.check_moz, job, result, settings))
        else:
            result.stage = 'done'
            self.emit(job, result)

    def check_moz(self, job, result, settings):
        if self.stop.is_set():
            raise Cancelled()
        if result.moz_state == 'reserved':
            result.moz_state = 'uncertain'
            result.errors['moz'] = 'Permintaan sebelumnya terputus; kuota tetap dihitung, tidak diulang otomatis'
        elif not (self.keys.get('moz_id') and self.keys.get('moz_secret')):
            result.moz_state = 'no_key'
        elif result.moz_state in ('failed', 'uncertain'):
            pass  # Explicit new job required; never silently repeat a potentially billable request.
        else:
            try:
                # Archive queue may take hours. Refresh availability BEFORE spending Moz rows.
                if not fresh(result.availability_at, 900):
                    try:
                        update(result, self.providers.availability(result.domain, force=True))
                    except SourceError as exc:
                        result.status = 'unknown'
                        result.moz_state = 'availability_failed'
                        result.errors['availability'] = str(exc)
                        result.stage = 'done'
                        self.emit(job, result)
                        return
                if result.status != 'available':
                    result.moz_state = 'not_available'
                else:
                    period = settings['moz_period'] or datetime.now(timezone.utc).strftime('%Y-%m')
                    if not self.store.reserve_row(period, settings['moz_budget'], settings['moz_reserve']):
                        result.moz_state = 'quota_reserved'
                    else:
                        result.moz_state = 'reserved'
                        self.emit(job, result)
                        update(result, self.providers.moz(result.domain))
                        result.errors.pop('moz', None)
            except SourceError as exc:
                result.moz_state = 'failed'
                result.errors['moz'] = str(exc)
        result.stage = 'done'
        self.emit(job, result)

    def close(self):
        self.pause()
        if self.thread:
            self.thread.join(timeout=1)
        if not self.alive():
            self.providers.close()
