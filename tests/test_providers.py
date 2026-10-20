import threading
from unittest.mock import Mock

import httpx
import pytest

from moz_checker.providers import Providers, RateGate, SourceError, Transport, retry_delay, safe_rdap_url


def provider(store):
    p = Providers(store, threading.Event(), {'opr_key': 'test', 'moz_id': 'id', 'moz_secret': 'secret'})
    p.bootstrap = {'services': [[['com'], ['https://rdap.example.com/']]]}
    p.dns_status = Mock(return_value='nxdomain')
    p.http.request = Mock()
    return p


def response(code, body):
    return httpx.Response(code, json=body)


@pytest.mark.parametrize(('code', 'body', 'dns', 'expected'), [
    (404, {'errorCode': 404}, 'nxdomain', 'available'),
    (404, {'errorCode': 404}, 'exists_no_soa', 'unknown'),
    (404, {'errorCode': 404}, 'error', 'unknown'),
    (404, {}, 'nxdomain', 'unknown'),
    (200, {'objectClassName': 'domain', 'ldhName': 'a.com'}, 'nxdomain', 'registered'),
    (200, {'objectClassName': 'domain', 'ldhName': 'a.com', 'status': ['redemption period']}, 'exists', 'redemption'),
    (200, {'objectClassName': 'domain', 'ldhName': 'a.com', 'status': ['pending delete']}, 'exists', 'pending delete'),
])
def test_double_evidence(store, code, body, dns, expected):
    p = provider(store)
    p.dns_status.return_value = dns
    p.http.request.return_value = response(code, body)
    assert p.availability('a.com')['status'] == expected
    p.close()


def test_unsupported_tld_and_invalid_object(store):
    p = provider(store)
    assert p.availability('a.id')['status'] == 'unknown'
    p.http.request.assert_not_called()
    p.http.request.return_value = response(200, {'objectClassName': 'domain', 'ldhName': 'wrong.com'})
    proof = p.availability('a.com')
    assert proof['status'] == 'unknown' and 'reason' in proof
    assert proof['dns'] == 'nxdomain'
    p.close()


def test_opr_batches_and_caches_zero(store):
    p = provider(store)
    domains = [f'a{i}.com' for i in range(101)]
    def request(*args, **kwargs):
        return response(200, {'response': [{'domain': d, 'status_code': 200, 'page_rank_decimal': 0}
                                          for _, d in kwargs['params']]})
    p.http.request.side_effect = request
    assert len(p.opr(domains)) == 101
    assert p.http.request.call_count == 2
    assert all(len(c.kwargs['params']) <= 100 for c in p.http.request.call_args_list)
    p.opr(domains)
    assert p.http.request.call_count == 2
    p.close()


def test_history_visible_keywords_and_snapshot_evidence(store):
    p = provider(store)
    p.http.request.side_effect = [response(200, [['timestamp', 'original'], ['20200101000000', 'http://a.com/']]),
        httpx.Response(200, text='<script>porn</script><title>Casino history</title>', headers={'content-type': 'text/html'})]
    result = p.history('a.com', 5, ['porn', 'casino'], True)
    assert result['history'] == 'invalid'
    assert result['sampled'] == 1
    assert [m['keyword'] for m in result['matches']] == ['casino']
    assert result['matches'][0]['snapshot'].startswith('https://web.archive.org/')
    p.close()


@pytest.mark.parametrize(('rows', 'empty_valid', 'expected'), [([], True, 'no_history'), ([], False, 'unchecked')])
def test_empty_archive_policy(store, rows, empty_valid, expected):
    p = provider(store)
    p.http.request.return_value = response(200, rows)
    assert p.history('a.com', 5, ['casino'], empty_valid)['history'] == expected
    p.close()


def test_snapshot_failure_never_valid(store):
    p = provider(store)
    p.http.request.side_effect = [response(200, [['timestamp', 'original'], ['20200101000000', 'http://a.com/']]), response(503, {})]
    assert p.history('a.com', 5, ['casino'], True)['history'] == 'unchecked'
    p.close()


def test_moz_missing_values_not_zero_and_no_retries(store):
    p = provider(store)
    p.http.request.return_value = response(200, {'results': [{'domain_authority': None, 'page_authority': 0, 'spam_score': -1}]})
    row = p.moz('a.com')
    assert row['da'] is None and row['pa'] == 0 and row['spam'] is None
    assert p.http.request.call_args.kwargs['attempts'] == 1
    p.close()


def test_transport_retry_after_and_credential_redirect():
    stop = threading.Event()
    t = Transport(stop)
    assert t.client.headers['accept-encoding'] == 'identity'
    t.client.close()
    requests = []
    def handle(request):
        requests.append(request)
        if len(requests) == 1:
            return httpx.Response(429, headers={'Retry-After': '0'})
        return httpx.Response(302, headers={'Location': 'https://evil.example/'})
    t.client = httpx.Client(transport=httpx.MockTransport(handle), follow_redirects=False)
    r = t.request('GET', 'https://api.example/', headers={'API-OPR': 'secret'})
    assert r.status_code == 302 and len(requests) == 2
    assert all(r.url.host == 'api.example' for r in requests)
    t.close()


def test_cancel_and_safe_urls():
    stop = threading.Event()
    stop.set()
    from moz_checker.providers import Cancelled
    with pytest.raises(Cancelled):
        RateGate(10).wait(stop)
    for url in ['http://rdap.example/', 'https://127.0.0.1/', 'https://a:b@example.org/']:
        with pytest.raises(SourceError):
            safe_rdap_url(url, 'a.com')
    assert retry_delay('30', 1) == 30


def test_moz_network_error_is_not_retried():
    t = Transport(threading.Event())
    t.client.close()
    count = 0
    def fail(request):
        nonlocal count
        count += 1
        raise httpx.ReadTimeout('not a response', request=request)
    t.client = httpx.Client(transport=httpx.MockTransport(fail))
    with pytest.raises(SourceError):
        t.request('POST', 'https://lsapi.seomoz.com/v2/url_metrics', attempts=1, json={})
    assert count == 1
    t.close()


def test_malformed_json_is_source_error_not_worker_crash(store):
    p = provider(store)
    p.http.request.return_value = response(200, [])
    with pytest.raises(SourceError):
        p.opr(['a.com'])
    p.http.request.return_value = response(404, [])
    assert p.availability('a.com')['status'] == 'unknown'
    p.close()


def test_samples_span_archive_timeline(store):
    p = provider(store)
    entries = [[f'{2000+i}0101000000', 'http://a.com/'] for i in range(10)]
    p.http.request.side_effect = [response(200, [['timestamp', 'original']] + entries)] + [
        httpx.Response(200, text='<title>Garden</title>', headers={'content-type': 'text/html'}) for _ in range(5)]
    result = p.history('a.com', 5, ['casino'], True)
    assert result['history'] == 'valid'
    assert result['sampled'] == 5 and result['snapshots'] == 10
    urls = [c.args[1] for c in p.http.request.call_args_list[1:]]
    assert '20000101000000' in urls[0]
    assert '20090101000000' in urls[-1]
    p.close()


def test_bootstrap_failure_preserves_dns_evidence(store):
    p = provider(store)
    p.bootstrap = None
    p.http.request.side_effect = SourceError('IANA HTTP 403')
    result = p.availability('a.com')
    assert result['status'] == 'unknown'
    assert result['dns'] == 'nxdomain'
    assert '403' in result['reason']
    p.close()


def test_unregistered_subdomain_cannot_be_reported_available(store):
    p = provider(store)
    p.http.request.return_value = response(404, {'errorCode': 404})
    assert p.availability('nonexistent.example.com')['status'] == 'unknown'
    p.http.request.assert_not_called()
    p.close()
