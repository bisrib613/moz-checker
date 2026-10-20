import hashlib
import importlib.util
import json
from pathlib import Path
import tempfile
import unittest
import zipfile

MODULE = Path(__file__).resolve().parents[1] / 'publish-release.py'
spec = importlib.util.spec_from_file_location('publisher', MODULE)
publisher = importlib.util.module_from_spec(spec)
spec.loader.exec_module(publisher)


class PublicationGateTests(unittest.TestCase):
    def setUp(self):
        self.temp = tempfile.TemporaryDirectory()
        self.addCleanup(self.temp.cleanup)
        self.root = Path(self.temp.name)
        self.reviewed = {'build_run_id': '123', 'source_commit': 'a' * 40, 'package_sha256': 'b' * 64}
        self.policy = {'publishing_blocked': True, 'reviewed_releases': {'v0.3.0': self.reviewed}}
        self.report = {
            'status': 'protected_startup_completed_no_detections', 'appExecuted': True,
            'appExitCode': 0, 'scanExitCode': 0, 'detections': [],
            'defender': dict.fromkeys(['AMServiceEnabled', 'AntivirusEnabled', 'RealTimeProtectionEnabled', 'BehaviorMonitorEnabled'], True),
            'finalDefender': {'RealTimeProtectionEnabled': True, 'BehaviorMonitorEnabled': True},
            'applicableExclusions': {'paths': [], 'extensions': [], 'processes': []},
            'firewallProfiles': [{'Enabled': 1}],
        }
        self.payload = {'MozChecker.exe': b'native host', 'MozChecker.dll': b'managed code'}
        self.report['exeSha256'] = hashlib.sha256(self.payload['MozChecker.exe']).hexdigest()
        self.report['payloadFiles'] = [{'path': k, 'bytes': len(v), 'sha256': hashlib.sha256(v).hexdigest()} for k, v in self.payload.items()]
        (self.root / 'smoke-report.json').write_text(json.dumps({'ui': 'WPF', 'status': 'passed', 'checks': ['IDN/PSL', 'eight columns', 'available-only results', 'registered log', 'search', 'reset', 'CSV button states']}))

    def validate(self, payload=None):
        (self.root / 'report.json').write_text(json.dumps(self.report))
        with zipfile.ZipFile(self.root / 'MozChecker-Windows-x64.zip', 'w') as archive:
            for name, data in (self.payload if payload is None else payload).items():
                archive.writestr('MozChecker/' + name, data)
        return publisher.verify_security_report(self.root, self.root)

    def test_reviewed_exception_does_not_unblock_old_or_unknown_releases(self):
        self.assertEqual(self.reviewed, publisher.release_permission(self.policy, 'v0.3.0', '123'))
        for tag in ('v0.2.0', 'v0.3.1', ''):
            with self.assertRaises(RuntimeError):
                publisher.release_permission(self.policy, tag, '123')

    def test_reviewed_release_rejects_a_different_build(self):
        with self.assertRaises(RuntimeError):
            publisher.release_permission(self.policy, 'v0.3.0', '124')

    def test_global_unblock_alone_does_not_authorize_artifacts(self):
        with self.assertRaises(RuntimeError):
            publisher.release_permission({'publishing_blocked': False}, 'v0.3.0', '123')

    def test_protected_payload_matches(self):
        self.assertEqual('passed', self.validate()['wpf_smoke']['status'])

    def test_modified_dll_is_rejected_even_if_host_is_unchanged(self):
        with self.assertRaises(RuntimeError):
            self.validate(self.payload | {'MozChecker.dll': b'changed code'})

    def test_untested_extra_file_is_rejected(self):
        with self.assertRaises(RuntimeError):
            self.validate(self.payload | {'extra.dll': b'untested'})

    def test_disabled_realtime_or_applicable_exclusion_is_rejected(self):
        self.report['defender']['RealTimeProtectionEnabled'] = False
        with self.assertRaises(RuntimeError):
            self.validate()
        self.report['defender']['RealTimeProtectionEnabled'] = True
        self.report['applicableExclusions']['paths'] = ['C:\\']
        with self.assertRaises(RuntimeError):
            self.validate()

    def test_detection_or_failed_smoke_is_rejected(self):
        self.report['detections'] = [{'ThreatID': 1}]
        with self.assertRaises(RuntimeError):
            self.validate()
        self.report['detections'] = []
        self.report['appExitCode'] = 1
        with self.assertRaises(RuntimeError):
            self.validate()


if __name__ == '__main__':
    unittest.main()
