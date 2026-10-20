# Windows executable detection — investigation open

A user reported Microsoft Defender blocking/quarantining `MozChecker.exe` as **Behavior:Win32/DefenseEvasion.A!ml**, severity **Severe**. This is a behavioral detection, not merely SmartScreen warning about an unknown publisher. The user confirmed Windows 11. The exact detected release version has not yet been confirmed.

**Do not allow, restore, run the quarantined file, add exclusions, or disable antivirus. No replacement binary has been cleared for distribution.** Automatic publication is blocked by `release-security.json` and the publisher checks this before calling GitHub. A warning is present on the v0.2.0 release. Keep the block until the report is resolved; do not remove it merely because functional tests pass or another scan is negative.

## Published sample under investigation

- Release: v0.2.0, source commit `b284da7eef19fd5d2eb0797ffc64d61790e70789`.
- ZIP SHA-256: `5d8a69a7d94aba30dbd4cdb2294295227deb649fd3d1879d9c4cb0776f1fd95f`.
- EXE SHA-256: `5b86ee9c80ccfc176937da27774e7de7cb0fdc22d0020ff17fa4e08045256488`.
- Packaging: PyInstaller 6.22.3, onedir/windowed. EXE is unsigned. Being unsigned alone does not establish the cause of this behavioral detection.

## Evidence collected

1. The downloaded published ZIP matches its previously recorded release digest.
2. The nine packaged application modules semantically match bytecode compiled from the tested source commit. This validates our application-code provenance, not every dependency or runtime behavior.
3. The bootloader executable `.text` section matches `runw.exe` from the locked official Windows PyInstaller wheel (wheel SHA-256 `500bd58c7bf7e584a8435adccbd763a0b918d5c12b08d74ff50fd79b2915458b`). Other compared non-resource sections match except `.rdata` debug timestamp bytes, consistent with PyInstaller's `set_exe_build_timestamp`; resources/payload are expected to differ.
4. An initial Defender file scan found no detections, but the runner had real-time protection disabled and broad exclusions. That result is insufficient to resolve the user's report.
5. [Protected startup audit](https://github.com/bisrib613/moz-checker/actions/runs/39393890069): enabled real-time and behavior monitoring, removed applicable broad exclusions only on a disposable GitHub VM, checked the unchanged published EXE hash, scanned the package and performed a native Windows Qt `--smoke` startup with a clean profile and no API keys. Scan/exit codes were 0, no detections were returned, and both protections remained enabled at the end. See [condensed evidence](security/v0.2.0-defender-evidence.json).

**Limitations:** GitHub uses Windows Server, not the user's Windows 11 endpoint. This audit had `MAPSReporting=0` (cloud reporting off) and `SubmitSamplesConsent=2`; those privacy settings were not relaxed. It exercised startup, not every feature. The earlier audit configured an outbound firewall rule but did not separately record effective profile state; the helper now verifies profile enablement too. None of these results proves safety or establishes a false positive.

## Next evidence needed

- Confirm the downloaded release version and (if available without restoring the EXE) original ZIP hash.
- Compare endpoint Defender engine/security-intelligence versions and affected-item details with the audit evidence.
- Obtain a vendor determination through [Microsoft's official file-submission portal](https://www.microsoft.com/en-us/wdsi/filesubmission), using the published sample, hashes, detection name and source/build details. No sample or support message has been submitted on the user's behalf.

Do not repack/rename the executable just to escape detection, sign an unresolved sample as a substitute for analysis, or describe old functional CI as antivirus clearance. Future build validation enables protection, verifies coverage and records Defender evidence before executing a packaged smoke test; it fails instead of claiming a clean result if protection/coverage is unavailable. Publication remains on hold even if that limited check passes.

## C# / WPF development migration

The user agreed to migrate the Windows application to C#/.NET WPF. The development 0.3.0 package contains no Python runtime, PyInstaller or Qt, and requires Microsoft .NET 10 Desktop Runtime x64 separately. This engineering change does not identify the cause of the original behavioral detection.

[Final native build](https://github.com/bisrib613/moz-checker/actions/runs/39400588371) from `aa5b912d58abaa115bd8abe674d0ff926ff0d9e3` passed 37 core tests on both Windows and Linux. Protected Windows startup/scan completed with no detections on that runner; real-time and behavior protection remained active. All 21 managed/native/license files tested by the helper match the packaged ZIP by SHA-256. The ZIP is 1,607,957 bytes, excluding the separately installed runtime. [Recorded evidence](security/v0.3.0-wpf-evidence.json) includes the package hash, DLL/EXE hashes, Defender settings and limitations.

The runner uses Windows Server, cloud reporting remains off, no real API keys were used, and this does not reproduce the user's Windows 11 endpoint or cover every application action. No sample has been submitted to Microsoft on the user's behalf. Public release remains on hold.
