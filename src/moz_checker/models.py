from dataclasses import dataclass, field, asdict
from datetime import datetime, timezone
import csv
import io
import re
from urllib.parse import urlsplit

import idna
import tldextract

# Bundled Public Suffix List: no hidden network request or cache write.
_SUFFIXES = tldextract.TLDExtract(suffix_list_urls=(), cache_dir=None, include_psl_private_domains=False)


def registrable(host: str) -> bool:
    extracted = _SUFFIXES(host)
    return bool(extracted.suffix and extracted.domain and not extracted.subdomain)


def now() -> str:
    return datetime.now(timezone.utc).isoformat()


def fresh(timestamp: str | None, seconds: int) -> bool:
    if not timestamp:
        return False
    try:
        return 0 <= (datetime.now(timezone.utc) - datetime.fromisoformat(timestamp)).total_seconds() < seconds
    except (ValueError, TypeError):
        return False


def normalize(raw: str) -> str:
    raw = raw.strip().strip('"\'').lower()
    if not raw:
        raise ValueError("Domain kosong")
    parsed = urlsplit(raw if '://' in raw else '//' + raw)
    if parsed.username or parsed.password or parsed.port:
        raise ValueError("Domain tidak boleh berisi kredensial atau port")
    host = (parsed.hostname or '').rstrip('.')
    if host.startswith('www.'):
        host = host[4:]
    host = idna.encode(host, uts46=True).decode('ascii')
    labels = host.split('.')
    if len(host) > 253 or len(labels) < 2 or all(x.isdigit() for x in labels):
        raise ValueError("Masukkan nama domain, bukan alamat IP")
    if any(not re.fullmatch(r'[a-z0-9](?:[a-z0-9-]{0,61}[a-z0-9])?', x) for x in labels):
        raise ValueError("Nama domain tidak valid")
    if labels[-1].isdigit():
        raise ValueError("TLD tidak valid")
    extracted = _SUFFIXES(host)
    if extracted.suffix and not registrable(host):
        raise ValueError('Masukkan domain registrable; subdomain dan public suffix tidak dapat dibeli')
    return host


@dataclass
class ImportResult:
    domains: list[str] = field(default_factory=list)
    duplicates: int = 0
    invalid: list[str] = field(default_factory=list)


def parse_domains(text: str, limit: int = 1000) -> ImportResult:
    result = ImportResult()
    seen = set()
    rows = list(csv.reader(io.StringIO(text)))
    if not rows:
        return result
    header = [v.strip().lower() for v in rows[0]]
    column = next((header.index(h) for h in ('domain', 'domains', 'url') if h in header), None)
    if column is not None:
        values = [r[column] for r in rows[1:] if len(r) > column]
    else:
        values = [v for row in rows for cell in row for v in re.split(r'[;\s]+', cell) if v]
    for value in values:
        try:
            domain = normalize(value)
        except (ValueError, idna.IDNAError):
            result.invalid.append(value)
            continue
        if domain in seen:
            result.duplicates += 1
        else:
            seen.add(domain)
            result.domains.append(domain)
    if len(result.domains) > limit:
        raise ValueError(f"Maksimal {limit:,} domain unik per hunting")
    return result


@dataclass
class Result:
    domain: str
    stage: str = 'queued'
    status: str = 'unknown'
    rdap_http: int | None = None
    rdap_url: str = ''
    dns: str = ''
    expires: str = ''
    registrar: str = ''
    availability_at: str = ''
    opr: float | None = None
    opr_at: str = ''
    history: str = ''
    snapshots: int | None = None
    sampled: int = 0
    matches: list[dict] = field(default_factory=list)
    history_at: str = ''
    da: float | None = None
    pa: float | None = None
    spam: float | None = None
    moz_at: str = ''
    moz_state: str = ''
    errors: dict = field(default_factory=dict)

    def to_dict(self):
        return asdict(self)

    @property
    def ready(self):
        return self.status == 'available' and fresh(self.availability_at, 900) and self.history in ('valid', 'no_history')
