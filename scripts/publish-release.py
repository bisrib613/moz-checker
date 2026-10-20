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
from pathlib import PurePosixPath


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


def release_permission(security, tag, run_id):
    reviewed = security.get('reviewed_releases', {}).get(tag)
    if reviewed is not None:
        if reviewed.get('build_run_id') != run_id or not re.fullmatch('[0-9a-f]{40}', reviewed.get('source_commit', '')) or not re.fullmatch('[0-9a-f]{64}', reviewed.get('package_sha256', '')):
            raise RuntimeError('Release does not match the reviewed build')
        return reviewed
    if security.get('publishing_blocked', True):
        raise RuntimeError('Release publication on hold: ' + security.get('reason', 'security review required'))
    raise RuntimeError('An explicit reviewed artifact is required for publication')


def verify_security_report(root, package_root):
    report = json.loads((root / 'report.json').read_text(encoding='utf-8-sig'))
    smoke = json.loads((root / 'smoke-report.json').read_text(encoding='utf-8-sig'))
    if not (report.get('status') == 'protected_startup_completed_no_detections'
            and report.get('appExecuted') is True and report.get('appExitCode') == 0
            and report.get('scanExitCode') == 0 and report.get('detections') == []):
        raise RuntimeError('Protected startup evidence did not pass')
    for field in ('AMServiceEnabled', 'AntivirusEnabled', 'RealTimeProtectionEnabled', 'BehaviorMonitorEnabled'):
        if report.get('defender', {}).get(field) is not True:
            raise RuntimeError('Defender coverage was unavailable')
    for field in ('RealTimeProtectionEnabled', 'BehaviorMonitorEnabled'):
        if report.get('finalDefender', {}).get(field) is not True:
            raise RuntimeError('Defender protection did not remain enabled')
    exclusions = report.get('applicableExclusions', {})
    if any(exclusions.get(key) for key in ('paths', 'extensions', 'processes')) and report.get('exclusionsRemovedOnDisposableRunner') is not True:
        raise RuntimeError('Test payload was excluded from scanning')
    profiles = report.get('firewallProfiles', [])
    if not profiles or not all(x.get('Enabled') in (True, 1) for x in profiles):
        raise RuntimeError('Startup containment was unavailable')
    checks = {'IDN/PSL', 'eight columns', 'available-only results', 'registered log', 'search', 'reset', 'CSV button states'}
    if smoke.get('ui') != 'WPF' or smoke.get('status') != 'passed' or not checks.issubset(smoke.get('checks', [])):
        raise RuntimeError('Native WPF smoke checks missing')
    with zipfile.ZipFile(package_root / 'MozChecker-Windows-x64.zip') as archive:
        names = [entry.filename for entry in archive.infolist() if not entry.is_dir()]
        payload = report.get('payloadFiles', [])
        expected = set()
        for entry in payload:
            path = entry['path'].replace('\\', '/')
            relative = PurePosixPath(path)
            if relative.is_absolute() or '..' in relative.parts or ':' in path:
                raise RuntimeError('Unsafe payload path')
            name = 'MozChecker/' + path
            if name in expected:
                raise RuntimeError('Duplicate payload evidence')
            expected.add(name)
            data = archive.read(name)
            if len(data) != entry['bytes'] or hashlib.sha256(data).hexdigest() != entry['sha256']:
                raise RuntimeError('Published payload differs from protected test')
        if len(names) != len(set(names)) or set(names) != expected:
            raise RuntimeError('Protected payload file set differs from package')
        if hashlib.sha256(archive.read('MozChecker/MozChecker.exe')).hexdigest() != report['exeSha256']:
            raise RuntimeError('Protected EXE hash mismatch')
    return {'defender_report': report, 'wpf_smoke': smoke}


def main():
    security = json.loads(Path('release-security.json').read_text())
    reviewed = release_permission(security, os.environ.get('RELEASE_TAG', ''), os.environ.get('BUILD_RUN_ID', ''))
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
    if commit != reviewed['source_commit']:
        raise RuntimeError('Build source does not match reviewed artifact')
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
        if digest != reviewed['package_sha256']:
            raise RuntimeError('Package does not match the reviewed digest')
        evidence_root = root / 'security'
        subprocess.run(['gh', 'run', 'download', run_id, '--repo', repository,
                        '--name', 'Windows-security-validation', '--dir', str(evidence_root)], check=True)
        evidence = verify_security_report(evidence_root, root)
        evidence.update(build_run=f'https://github.com/{repository}/actions/runs/{run_id}',
                        source_commit=commit, package_sha256=digest,
                        limitation='Limited runner testing does not resolve the earlier endpoint detection or establish a false positive.')
        evidence_file = root / 'SECURITY-VALIDATION.json'
        evidence_file.write_text(json.dumps(evidence, indent=2) + '\n')
        subprocess.run(['gh', 'release', 'create', tag, str(root / 'MozChecker-Windows-x64.zip'),
                        str(root / 'SHA256SUMS.txt'), str(evidence_file), '--repo', repository, '--target', commit,
                        '--prerelease', '--title', f'{tag} — Windows WPF Preview',
                        '--notes-file', str(notes)], check=True)
        release = gh_json('api', f'repos/{repository}/releases/tags/{tag}')
        assets = {asset['name']: asset for asset in release['assets']}
        expected_names = {'MozChecker-Windows-x64.zip', 'SHA256SUMS.txt', 'SECURITY-VALIDATION.json'}
        if release['draft'] or not expected_names.issubset(assets):
            raise RuntimeError('Published assets missing')
        for name in expected_names:
            asset = assets[name]
            file = root / name
            if asset['state'] != 'uploaded' or asset['size'] != file.stat().st_size:
                raise RuntimeError('Uploaded asset size/state mismatch')
            if asset.get('digest') and asset['digest'] != 'sha256:' + hashlib.sha256(file.read_bytes()).hexdigest():
                raise RuntimeError('GitHub asset digest mismatch')
        asset = assets['MozChecker-Windows-x64.zip']
        if asset['state'] != 'uploaded' or asset['size'] != (root / asset['name']).stat().st_size:
            raise RuntimeError('Uploaded asset size/state mismatch')
        if asset.get('digest') and asset['digest'] != 'sha256:' + digest:
            raise RuntimeError('GitHub asset digest mismatch')
        print('Published:', release['html_url'])
        print('Verified SHA256:', digest)


if __name__ == '__main__':
    main()
