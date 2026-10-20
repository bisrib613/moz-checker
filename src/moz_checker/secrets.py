import sys
import keyring

SERVICE = 'MozChecker'
NAMES = ('opr_key', 'moz_id', 'moz_secret')


def backend():
    if sys.platform == 'win32':
        from keyring.backends.Windows import WinVaultKeyring
        return WinVaultKeyring()
    candidate = keyring.get_keyring()
    # Never fall back to plaintext keyrings or environment variables.
    if not candidate.__class__.__module__.startswith(('keyring.backends.SecretService', 'keyring.backends.macOS')):
        raise RuntimeError('Penyimpanan key aman tidak tersedia. Di cloud, jalankan tanpa key; di Windows gunakan Credential Manager.')
    return candidate


def get(name):
    if name not in NAMES:
        raise ValueError('Unknown credential')
    try:
        return backend().get_password(SERVICE, name) or ''
    except Exception:
        return ''


def save(name, value):
    if name not in NAMES:
        raise ValueError('Unknown credential')
    vault = backend()
    if value:
        vault.set_password(SERVICE, name, value)
    elif vault.get_password(SERVICE, name):
        vault.delete_password(SERVICE, name)
