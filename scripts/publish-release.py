"""Publish only a verified Windows artifact from a successful CI run on this repository's main."""
import base64
import hashlib
import json
import os
from pathlib import Path
import re
import struct
import subprocess
import tempfile
import xml.etree.ElementTree as ET
import zipfile


def gh_json(*args):
    result = subprocess.run(['gh', *args], check=True, capture_output=True, text=True)
    return json.loads(result.stdout)


def verify_package(root):
    archive = root / 'MozChecker-Windows-x64.zip'
    checksum = root / 'SHA256SUMS.txt'
    words = checksum.read_text(encoding='ascii').split()
    if len(words) != 2 or words[1] != archive.name or not re.fullmatch('[0-9a-f]{64}', words[0]):
        raise RuntimeError('Invalid checksum manifest')
    actual = hashlib.sha256(archive.read_bytes()).hexdigest()
    if actual != words[0]:
        raise RuntimeError('Package checksum mismatch')
    with zipfile.ZipFile(archive) as contents:
        if contents.testzip() is not None:
            raise RuntimeError('Package ZIP CRC failure')
        executable = contents.read('MozChecker/MozChecker.exe')
        offset = struct.unpack_from('<I', executable, 0x3c)[0]
        if (executable[:2] != b'MZ' or executable[offset:offset + 4] != b'PE\x00\x00'
                or struct.unpack_from('<H', executable, offset + 4)[0] != 0x8664):
            raise RuntimeError('Not a Windows x64 executable')
        required = {'MozChecker/MozChecker.dll', 'MozChecker/MozChecker.runtimeconfig.json', 'MozChecker/MozChecker.Core.dll', 'MozChecker/MozChecker.deps.json'}
        if not required.issubset(contents.namelist()):
            raise RuntimeError('Native .NET desktop package is incomplete')
        runtime = json.loads(contents.read('MozChecker/MozChecker.runtimeconfig.json'))
        frameworks = runtime['runtimeOptions'].get('frameworks', [])
        if not any(x['name'] == 'Microsoft.WindowsDesktop.App' and x['version'].startswith('10.') for x in frameworks):
            raise RuntimeError('WPF .NET 10 Desktop Runtime requirement missing')
    return actual


def main():
    security = json.loads(Path('release-security.json').read_text())
    if security.get('publishing_blocked', True):
        raise RuntimeError('Release publication on hold: ' + security.get('reason', 'security review required'))
    repository = os.environ['GH_REPO']
    tag = os.environ['RELEASE_TAG']
    run_id = os.environ['BUILD_RUN_ID']
    if not re.fullmatch(r'[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+', repository):
        raise RuntimeError('Invalid repository')
    if not re.fullmatch(r'v\d+\.\d+\.\d+', tag) or not run_id.isdigit():
        raise RuntimeError('Use a semantic release tag and numeric build run ID')
    run = gh_json('api', f'repos/{repository}/actions/runs/{run_id}')
    if not (run['status'] == 'completed' and run['conclusion'] == 'success'
            and run['path'] == '.github/workflows/check.yml' and run['event'] == 'push'
            and run['head_branch'] == 'main' and run['head_repository']['full_name'] == repository):
        raise RuntimeError('Build must be a successful main push from the repository CI workflow')
    commit = run['head_sha']
    package = gh_json('api', f'repos/{repository}/contents/dotnet/MozChecker.Desktop/MozChecker.Desktop.csproj?ref={commit}')
    version = ET.fromstring(base64.b64decode(package['content'])).findtext('PropertyGroup/Version')
    if tag != 'v' + version:
        raise RuntimeError('Tag does not match tested application version')
    existing = subprocess.run(['gh', 'release', 'view', tag, '--repo', repository], capture_output=True)
    if existing.returncode == 0:
        raise RuntimeError('Release already exists; refusing to overwrite it')
    notes = Path('docs/releases') / (tag + '.md')
    if not notes.is_file():
        raise RuntimeError('Reviewed release notes are missing')
    with tempfile.TemporaryDirectory() as directory:
        root = Path(directory)
        subprocess.run(['gh', 'run', 'download', run_id, '--repo', repository,
                        '--name', 'MozChecker-Windows', '--dir', str(root)], check=True)
        digest = verify_package(root)
        subprocess.run(['gh', 'release', 'create', tag, str(root / 'MozChecker-Windows-x64.zip'),
                        str(root / 'SHA256SUMS.txt'), '--repo', repository, '--target', commit,
                        '--prerelease', '--title', f'{tag} — Windows WPF Preview',
                        '--notes-file', str(notes)], check=True)
        release = gh_json('api', f'repos/{repository}/releases/tags/{tag}')
        assets = {asset['name']: asset for asset in release['assets']}
        expected_names = {'MozChecker-Windows-x64.zip', 'SHA256SUMS.txt'}
        if release['draft'] or not expected_names.issubset(assets):
            raise RuntimeError('Published assets missing')
        asset = assets['MozChecker-Windows-x64.zip']
        if asset['state'] != 'uploaded' or asset['size'] != (root / asset['name']).stat().st_size:
            raise RuntimeError('Uploaded asset size/state mismatch')
        if asset.get('digest') and asset['digest'] != 'sha256:' + digest:
            raise RuntimeError('GitHub asset digest mismatch')
        print('Published:', release['html_url'])
        print('Verified SHA256:', digest)


if __name__ == '__main__':
    main()
