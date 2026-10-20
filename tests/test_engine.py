from datetime import datetime, timezone
import threading

from moz_checker.engine import Engine
from moz_checker.models import now
from moz_checker.providers import SourceError


class FakeProviders:
    def __init__(self, store, stop, keys):
        self.calls = []
        self.stop = stop

    def opr(self, domains):
        return {d: {'opr': 4, 'opr_at': now()} for d in domains}

    def availability(self, domain, force=False):
        self.calls.append(('availability', domain))
        return {'status': 'registered' if domain.startswith('registered') else 'available',
                'rdap_http': 404, 'dns': 'nxdomain', 'availability_at': now()}

    def history(self, domain, *args):
        if domain.startswith('error'):
            raise SourceError('HTTP 503')
        return {'history': 'invalid' if domain.startswith('bad') else 'valid', 'snapshots': 1,
                'history_at': now()}

    def moz(self, domain):
        self.calls.append(('moz', domain))
        return {'da': 25, 'pa': 20, 'spam': None, 'moz_at': now(), 'moz_state': 'ok'}

    def close(self):
        pass


def run(engine):
    engine.start()
    engine.thread.join(10)
    assert not engine.alive()
    assert not engine.failure


def test_funnel_errors_quota_and_multiple_jobs(store):
    store.configure({'moz_budget': 2, 'moz_reserve': 1})
    first = store.new_job(['registered.com', 'bad.com', 'error.com', 'good.com', 'good2.com'])
    second = store.new_job(['good3.com'])
    engine = Engine(store, {'moz_id': 'id', 'moz_secret': 'secret'}, provider_factory=FakeProviders)
    run(engine)
    rows = {r.domain: r for r in store.results(first)}
    assert rows['registered.com'].history == ''
    assert rows['bad.com'].history == 'invalid'
    assert rows['error.com'].history == 'unchecked'
    assert len([c for c in engine.providers.calls if c[0] == 'moz']) == 1
    finalists = [rows['good.com'], rows['good2.com']]
    assert sum(r.da == 25 for r in finalists) == 1
    assert sum(r.moz_state == 'quota_reserved' for r in finalists) == 1
    assert store.results(second)[0].moz_state == 'quota_reserved'
    assert all(state == 'completed' for _, _, state in store.jobs())
    assert store.used(datetime.now(timezone.utc).strftime('%Y-%m')) == 1


def test_no_keys_no_moz_and_completed_not_rerun(store):
    job = store.new_job(['good.com'])
    engine = Engine(store, {}, provider_factory=FakeProviders)
    run(engine)
    assert store.results(job)[0].moz_state == 'no_key'
    assert not any(c[0] == 'moz' for c in engine.providers.calls)
    before = len(engine.providers.calls)
    store.state(job, 'queued')
    run(engine)
    assert len(engine.providers.calls) == before


def test_interrupted_moz_is_not_charged_twice(store):
    job = store.new_job(['good.com'])
    r = store.results(job)[0]
    r.moz_state = 'reserved'
    store.save(job, r)
    engine = Engine(store, {'moz_id': 'id', 'moz_secret': 'secret'}, provider_factory=FakeProviders)
    run(engine)
    assert store.results(job)[0].moz_state == 'uncertain'
    assert not any(c[0] == 'moz' for c in engine.providers.calls)


def test_pause_resume_from_completed_domain(store):
    job = store.new_job(['registered.com', 'good.com'])
    r = store.results(job)[0]
    r.stage = 'done'
    store.save(job, r)
    engine = Engine(store, {}, provider_factory=FakeProviders)
    engine.stop = threading.Event()
    run(engine)
    assert ('availability', 'registered.com') not in engine.providers.calls
    assert store.results(job)[1].stage == 'done'


def test_moz_refreshes_stale_availability_before_charging(store):
    class RegisteredOnRefresh(FakeProviders):
        def availability(self, domain, force=False):
            return {'status': 'registered', 'availability_at': now()}
    from moz_checker.models import Result
    engine = Engine(store, {'moz_id': 'id', 'moz_secret': 'secret'}, provider_factory=RegisteredOnRefresh)
    job = store.new_job(['good.com'])
    r = Result('good.com', status='available', history='valid', availability_at='2000-01-01T00:00:00+00:00')
    engine.check_moz(job, r, store.settings())
    assert r.moz_state == 'not_available'
    assert not any(c[0] == 'moz' for c in engine.providers.calls)
    assert store.used(datetime.now(timezone.utc).strftime('%Y-%m')) == 0


def test_thousand_domain_job_no_key(store):
    job = store.new_job([f'registered{i}.com' for i in range(1000)])
    engine = Engine(store, {}, provider_factory=FakeProviders)
    run(engine)
    assert len(store.results(job)) == 1000
    assert all(r.stage == 'done' and r.status == 'registered' for r in store.results(job))


def test_pause_interrupts_then_resumes(store):
    from moz_checker.providers import Cancelled
    entered = threading.Event()
    class Blocking(FakeProviders):
        def availability(self, domain, force=False):
            entered.set()
            self.stop.wait(5)
            raise Cancelled()
    job = store.new_job(['good.com', 'good2.com'])
    engine = Engine(store, {}, provider_factory=Blocking)
    engine.start()
    assert entered.wait(2)
    engine.pause()
    engine.thread.join(5)
    assert not engine.alive()
    assert store.jobs()[0][2] == 'paused'
    store.state(job, 'queued')
    resumed = Engine(store, {}, provider_factory=FakeProviders)
    run(resumed)
    assert all(r.stage == 'done' for r in store.results(job))


def test_opr_never_spends_rows_on_registered_domains(store):
    class TrackOPR(FakeProviders):
        def opr(self, domains):
            assert all(not d.startswith('registered') for d in domains)
            self.calls.extend(('opr', d) for d in domains)
            return super().opr(domains)
    store.new_job(['registered.com', 'good.com', 'bad.com'])
    engine = Engine(store, {'opr_key': 'key'}, provider_factory=TrackOPR)
    run(engine)
    assert {d for source, d in engine.providers.calls if source == 'opr'} == {'good.com', 'bad.com'}


def test_refresh_failure_does_not_consume_or_poison_moz_retry(store):
    from moz_checker.models import Result
    class FailedRefresh(FakeProviders):
        def availability(self, domain, force=False):
            raise SourceError('RDAP unavailable')
    job = store.new_job(['good.com'])
    engine = Engine(store, {'moz_id': 'id', 'moz_secret': 'secret'}, provider_factory=FailedRefresh)
    result = Result('good.com', status='available', history='valid')
    engine.check_moz(job, result, store.settings())
    assert result.status == 'unknown'
    assert result.moz_state == 'availability_failed'
    assert store.used(datetime.now(timezone.utc).strftime('%Y-%m')) == 0
