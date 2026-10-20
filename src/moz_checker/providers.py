"""Conservative adapters. No scraping of SEO tools, no credential-bearing redirects."""
import email.utils
from datetime import datetime, timezone
from html.parser import HTMLParser
import ipaddress
import re
import threading
import time
from urllib.parse import urlsplit

import dns.resolver
import httpx

from .models import now, registrable


class Cancelled(Exception):
    pass


class SourceError(Exception):
    pass


class RateGate:
    def __init__(self, interval):
        self.interval = interval
        self.lock = threading.Lock()
        self.next = 0.0

    def wait(self, stop):
        with self.lock:
            if stop.wait(max(0, self.next - time.monotonic())):
                raise Cancelled()
            self.next = time.monotonic() + self.interval


def retry_delay(value, attempt):
    try:
        return max(0, float(value))
    except (TypeError, ValueError):
        try:
            return max(0, (email.utils.parsedate_to_datetime(value) - datetime.now(timezone.utc)).total_seconds())
        except (TypeError, ValueError, OverflowError):
            return 2 ** attempt


class Transport:
    def __init__(self, stop):
        self.stop = stop
        self.client = httpx.Client(timeout=httpx.Timeout(25, connect=10), follow_redirects=False,
                                   headers={'User-Agent': 'MozChecker/0.1 (+local desktop research)', 'Accept-Encoding': 'identity'})

    def request(self, method, url, gate=None, max_bytes=2_000_000, attempts=3, **kwargs):
        for attempt in range(attempts):
            if self.stop.is_set():
                raise Cancelled()
            if gate:
                gate.wait(self.stop)
            try:
                with self.client.stream(method, url, **kwargs) as response:
                    if response.status_code in (429, 502, 503, 504):
                        delay = retry_delay(response.headers.get('Retry-After'), attempt)
                        if delay > 120:
                            raise SourceError(f'HTTP {response.status_code}: Retry-After panjang; coba ulang nanti')
                        if self.stop.wait(delay):
                            raise Cancelled()
                        continue
                    chunks, size = [], 0
                    for chunk in response.iter_bytes():
                        if self.stop.is_set():
                            raise Cancelled()
                        size += len(chunk)
                        if size > max_bytes:
                            raise SourceError('Respons terlalu besar; tidak disimpulkan sebagai bersih')
                        chunks.append(chunk)
                    return httpx.Response(response.status_code, headers=response.headers,
                                          content=b''.join(chunks), request=response.request)
            except httpx.HTTPError as exc:
                # Avoid logging request headers or credentials.
                if attempt == attempts - 1:
                    raise SourceError(f'Koneksi gagal ({type(exc).__name__})') from None
                if self.stop.wait(2 ** attempt):
                    raise Cancelled()
        raise SourceError('Layanan sibuk setelah 3 percobaan; coba ulang nanti')

    def close(self):
        self.client.close()


def json_response(response, expected=dict):
    if response.status_code != 200:
        raise SourceError(f'HTTP {response.status_code}')
    try:
        value = response.json()
        if not isinstance(value, expected):
            raise SourceError('Struktur JSON tidak sesuai kontrak sumber')
        return value
    except ValueError:
        raise SourceError('Respons JSON tidak valid') from None


def safe_rdap_url(base, domain):
    parsed = urlsplit(base)
    if parsed.scheme != 'https' or not parsed.hostname or parsed.username or parsed.password:
        raise SourceError('Rute RDAP tidak aman')
    try:
        address = ipaddress.ip_address(parsed.hostname)
        if not address.is_global:
            raise SourceError('Rute RDAP non-publik ditolak')
    except ValueError:
        if '.' not in parsed.hostname or parsed.hostname.endswith(('.local', '.localhost')):
            raise SourceError('Rute RDAP non-publik ditolak') from None
    return base.rstrip('/') + '/domain/' + domain


class Providers:
    def __init__(self, store, stop, keys):
        self.store, self.stop, self.keys = store, stop, keys
        self.http = Transport(stop)
        self.archive_gate = RateGate(4.1)  # Shared by metadata AND snapshot content.
        self.opr_gate = RateGate(1.1)
        self.moz_gate = RateGate(10.1)
        self.bootstrap_lock = threading.Lock()
        self.bootstrap = None
        self.rdap_gates = {}

    def bootstrap_data(self):
        with self.bootstrap_lock:
            if self.bootstrap is None:
                self.bootstrap = self.store.cache_get('iana', 'bootstrap', 7 * 86400)
                if self.bootstrap is None:
                    self.bootstrap = json_response(self.http.request('GET', 'https://data.iana.org/rdap/dns.json'))
                    if not isinstance(self.bootstrap.get('services'), list):
                        raise SourceError('Bootstrap IANA tidak valid')
                    self.store.cache_put('iana', 'bootstrap', self.bootstrap)
            return self.bootstrap

    def dns_status(self, domain):
        resolver = dns.resolver.Resolver()
        resolver.lifetime = 5
        try:
            resolver.resolve(domain, 'SOA', search=False, raise_on_no_answer=True)
            return 'exists'
        except dns.resolver.NXDOMAIN:
            return 'nxdomain'
        except dns.resolver.NoAnswer:
            return 'exists_no_soa'
        except (dns.exception.DNSException, OSError):
            return 'error'

    def availability(self, domain, force=False):
        cached = None if force else self.store.cache_get('availability', domain, 900)
        if cached:
            return cached
        if not registrable(domain):
            return {'status': 'unknown', 'reason': 'Bukan domain registrable yang dikenali Public Suffix List'}
        result = {'status': 'unknown', 'rdap_http': None, 'rdap_url': '', 'dns': self.dns_status(domain),
                  'expires': '', 'registrar': '', 'availability_at': now()}
        try:
            tld = domain.rsplit('.', 1)[1]
            routes = [url for tlds, urls in self.bootstrap_data()['services'] if tld in tlds for url in urls
                      if url.startswith('https://')]
            if not routes:
                result['reason'] = 'TLD tanpa rute RDAP HTTPS; WHOIS belum didukung'
                return result
            url = safe_rdap_url(routes[0], domain)
            result['rdap_url'] = url
            with self.bootstrap_lock:
                gate = self.rdap_gates.setdefault(urlsplit(url).hostname, RateGate(0.5))
            response = self.http.request('GET', url, gate=gate)
            result.update(rdap_url=url, rdap_http=response.status_code)
            if response.status_code == 404:
                # A route from IANA plus a protocol-level RDAP error is required, not a generic web 404.
                try:
                    body = response.json()
                except ValueError:
                    body = {}
                if isinstance(body, dict) and body.get('errorCode') == 404 and result['dns'] == 'nxdomain':
                    result['status'] = 'available'
            elif response.status_code == 200:
                body = json_response(response)
                if body.get('objectClassName') != 'domain' or body.get('ldhName', '').lower().rstrip('.') != domain:
                    raise SourceError('RDAP bukan objek domain yang diminta')
                states = {str(x).lower().replace(' ', '').replace('-', '') for x in body.get('status', [])}
                result['status'] = ('pending delete' if 'pendingdelete' in states else
                                    'redemption' if 'redemptionperiod' in states else 'registered')
                result['expires'] = next((e.get('eventDate', '') for e in body.get('events', [])
                                          if e.get('eventAction') == 'expiration'), '')
                for entity in body.get('entities', []):
                    if 'registrar' in entity.get('roles', []):
                        card = entity.get('vcardArray', [])
                        props = card[1] if len(card) > 1 else []
                        result['registrar'] = next((p[3] for p in props if len(p) > 3 and p[0] == 'fn'), '')
            else:
                raise SourceError(f'RDAP HTTP {response.status_code}; status unknown')
        except SourceError as exc:
            return result | {'reason': str(exc)}
        self.store.cache_put('availability', domain, result)
        return result

    def opr(self, domains):
        output = {}
        pending = []
        for domain in domains:
            cached = self.store.cache_get('opr', domain, 30 * 86400)
            if cached is not None:
                output[domain] = cached
            else:
                pending.append(domain)
        if not pending or not self.keys.get('opr_key'):
            return output
        for start in range(0, len(pending), 100):
            batch = pending[start:start + 100]
            data = json_response(self.http.request('GET', 'https://openpagerank.domcop.com/api/v1.0/getPageRank',
                gate=self.opr_gate, headers={'API-OPR': self.keys['opr_key']},
                params=[('domains[]', d) for d in batch]))
            rows = data.get('response')
            if not isinstance(rows, list):
                raise SourceError('Struktur respons OPR tidak valid')
            for row in rows:
                if not isinstance(row, dict):
                    raise SourceError('Baris OPR tidak valid')
                domain = row.get('domain')
                if domain not in batch:
                    continue
                score = row.get('page_rank_decimal')
                if row.get('status_code') == 200 and isinstance(score, (int, float)) and 0 <= score <= 10:
                    value = {'opr': score, 'opr_at': now()}
                    output[domain] = value
                    self.store.cache_put('opr', domain, value)
        return output

    def history(self, domain, samples, keywords, empty_valid):
        response = self.http.request('GET', 'https://web.archive.org/cdx/search/cdx', gate=self.archive_gate,
            params={'url': domain + '/*', 'output': 'json', 'filter': ['statuscode:200', 'mimetype:text/html'],
                    'collapse': 'digest', 'fl': 'timestamp,original', 'limit': 10001}, max_bytes=3_000_000)
        rows = json_response(response, list)
        if not isinstance(rows, list) or (rows and rows[0] != ['timestamp', 'original']):
            raise SourceError('Metadata arsip tidak valid atau akses dikecualikan')
        entries = rows[1:] if rows else []
        if len(entries) >= 10001:
            raise SourceError('Arsip melebihi batas metadata; perlu pemeriksaan manual')
        result = {'snapshots': len(entries), 'sampled': 0, 'matches': [], 'history_at': now()}
        if not entries:
            return result | {'history': 'no_history' if empty_valid else 'unchecked'}
        count = min(samples, len(entries))
        indices = sorted({round(i * (len(entries) - 1) / max(1, count - 1)) for i in range(count)})
        failed = False
        for index in indices:
            entry = entries[index]
            if not isinstance(entry, list) or len(entry) != 2:
                raise SourceError('Entri arsip tidak valid')
            stamp, original = entry
            if not isinstance(stamp, str) or not isinstance(original, str):
                raise SourceError('Identitas snapshot tidak valid')
            if not re.fullmatch(r'\d{14}', stamp) or urlsplit(original).hostname not in (domain, 'www.' + domain):
                raise SourceError('Snapshot tidak cocok dengan domain')
            url = f'https://web.archive.org/web/{stamp}id_/{original}'
            try:
                snapshot = self.http.request('GET', url, gate=self.archive_gate)
                if snapshot.status_code != 200 or 'html' not in snapshot.headers.get('content-type', '').lower():
                    failed = True
                    continue
                parser = VisibleText()
                parser.feed(snapshot.text)
                text = ' '.join(parser.parts).casefold()
                result['sampled'] += 1
                for keyword in keywords:
                    word = keyword.strip().casefold()
                    if word and re.search(r'(?<!\w)' + re.escape(word) + r'(?!\w)', text):
                        result['matches'].append({'keyword': keyword, 'snapshot': url})
            except SourceError:
                failed = True
        result['history'] = 'invalid' if result['matches'] else ('unchecked' if failed else 'valid')
        return result

    def moz(self, domain):
        # Legacy Moz Links API v2: user must have an account entitled to this endpoint.
        data = json_response(self.http.request('POST', 'https://lsapi.seomoz.com/v2/url_metrics',
            gate=self.moz_gate, attempts=1, auth=(self.keys['moz_id'], self.keys['moz_secret']),
            json={'targets': ['https://' + domain + '/']}))
        rows = data.get('results')
        if not isinstance(rows, list) or not rows:
            raise SourceError('Struktur respons Moz tidak valid')
        row = rows[0]
        if not isinstance(row, dict):
            raise SourceError('Baris Moz tidak valid')
        result = {'moz_at': now(), 'moz_state': 'no_data'}
        for field, source in [('da', 'domain_authority'), ('pa', 'page_authority'), ('spam', 'spam_score')]:
            value = row.get(source)
            # A negative sentinel or missing field is not a zero score.
            result[field] = value if isinstance(value, (int, float)) and 0 <= value <= 100 else None
        if any(result.get(k) is not None for k in ('da', 'pa', 'spam')):
            result['moz_state'] = 'ok'
        return result

    def close(self):
        self.http.close()


class VisibleText(HTMLParser):
    def __init__(self):
        super().__init__(convert_charrefs=True)
        self.hidden = 0
        self.parts = []

    def handle_starttag(self, tag, attrs):
        if tag in ('script', 'style', 'noscript'):
            self.hidden += 1

    def handle_endtag(self, tag):
        if tag in ('script', 'style', 'noscript'):
            self.hidden = max(0, self.hidden - 1)

    def handle_data(self, data):
        if not self.hidden:
            self.parts.append(data)
