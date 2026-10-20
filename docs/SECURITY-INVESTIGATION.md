# Windows executable detection — investigation open

A user reported Microsoft Defender blocking/quarantining `MozChecker.exe` as **Behavior:Win32/DefenseEvasion.A!ml**, severity **Severe**. This is a behavioral detection, not merely SmartScreen warning about an unknown publisher. The user confirmed Windows 11. The exact detected release version has not yet been confirmed.

**Do not allow, restore, run the quarantined file, add exclusions, or disable antivirus. No vendor determination has cleared the reported Python sample or established a false positive.** Unreviewed publication is blocked by `release-security.json`; exceptions are limited to exact C# preview build/digest entries explicitly reviewed for publication. A warning is present on the v0.2.0 release. The default block and old-release warning remain. The C# preview exception does not close the Python investigation.

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

Do not repack/rename the executable just to escape detection, sign an unresolved sample as a substitute for analysis, or describe old functional CI as antivirus clearance. Future build validation enables protection, verifies coverage and records Defender evidence before executing a packaged smoke test; it fails instead of claiming a clean result if protection/coverage is unavailable. A passing check alone cannot authorize publication; an explicit version/build/source/digest review entry is also required.

## C# / WPF development migration

The user agreed to migrate the Windows application to C#/.NET WPF. The development 0.3.0 package contains no Python runtime, PyInstaller or Qt, and requires Microsoft .NET 10 Desktop Runtime x64 separately. This engineering change does not identify the cause of the original behavioral detection.

[Final native build](https://github.com/bisrib613/moz-checker/actions/runs/39400588371) from `aa5b912d58abaa115bd8abe674d0ff926ff0d9e3` passed 37 core tests on both Windows and Linux. Protected Windows startup/scan completed with no detections on that runner; real-time and behavior protection remained active. All 21 managed/native/license files tested by the helper match the packaged ZIP by SHA-256. The ZIP is 1,607,957 bytes, excluding the separately installed runtime. [Recorded evidence](security/v0.3.0-wpf-evidence.json) includes the package hash, DLL/EXE hashes, Defender settings and limitations.

The runner uses Windows Server, cloud reporting remains off, no real API keys were used, and this does not reproduce the user's Windows 11 endpoint or cover every application action. No sample has been submitted to Microsoft on the user's behalf. The user subsequently requested completion and publication. Only the exact v0.3.0 C# build was authorized as a preview, with these limitations disclosed; all unreviewed releases remain blocked.

## Preview publication decision

The explicit request to finish publishing authorizes v0.3.0 from build `39400588371`, source `aa5b912d58abaa115bd8abe674d0ff926ff0d9e3`, ZIP SHA-256 `abfb47607832ad174eb6b52ce2d7e8a014b8d08b46d024b3a078a247d3c6526f`. The publisher independently fetches the successful CI artifacts, verifies the pinned source/hash, active Defender evidence, WPF smoke and every packaged file. Release notes disclose that the old detection is unresolved and that runner checks are limited. The Python warning and default publication block remain; nothing is renamed, restored from quarantine or exempted from antivirus.

## Publication outcome

[v0.3.0 Preview](https://github.com/bisrib613/moz-checker/releases/tag/v0.3.0) was published with the unchanged reviewed ZIP, checksum manifest and `SECURITY-VALIDATION.json`. Integration permissions prevented release metadata creation, so the owner created the pinned draft; [workflow 39408332689](https://github.com/bisrib613/moz-checker/actions/runs/39408332689) verified and uploaded the assets. The downloaded draft assets, source, release notes and GitHub digests were independently checked before owner publication. Release ID: `418273912`; ZIP SHA-256 remains `abfb47607832ad174eb6b52ce2d7e8a014b8d08b46d024b3a078a247d3c6526f`. The Python investigation and default block for unreviewed releases remain open.

## 0.3.1 import fix publication

[v0.3.1 Preview](https://github.com/bisrib613/moz-checker/releases/tag/v0.3.1) publishes the immediate CSV parsing / large-list batching fix from source `610a22667ba523c53abe35f03184e4f1da051d17`, build `39413042597`. All 45 core tests passed on Windows and Linux; protected WPF smoke additionally checks immediate CSV extraction and retention of a 1,001-domain import. The complete payload matches the protected copy; ZIP SHA-256 is `918482ad6d1784ea02278e32860eed1090510dd666a5633f8b98116cac042e1a` (1,612,412 bytes). [Publisher workflow](https://github.com/bisrib613/moz-checker/actions/runs/39413465942) staged the reviewed assets; downloaded asset hashes, notes and source were independently checked before owner publication of release ID `418309532`. [Evidence](security/v0.3.1-wpf-evidence.json). Prior endpoint-investigation limitations remain unchanged.

## 0.3.2 file-size cap removal publication

[v0.3.2 Preview](https://github.com/bisrib613/moz-checker/releases/tag/v0.3.2) removes the 5 MB import rejection. Build `39413799087`, source `7e4be0def7dc67856196a32a8f1419e38b313085`: 45 core tests passed on each OS and protected WPF smoke passed on the runner. The exact reviewed ZIP is 1,612,341 bytes, SHA-256 `2ea977e8ec9fac4b8cc5ce5b711364199a987c76686adb984f83af62b77b820a`. Workflow `39414333645` verified/staged the assets; downloaded bytes, security evidence, source and release notes matched before owner publication of release ID `418315409`. [Evidence](security/v0.3.2-wpf-evidence.json). Prior investigation limitations remain unchanged.

## 0.4.0 full-list workspace publication

[v0.4.0 Preview](https://github.com/bisrib613/moz-checker/releases/tag/v0.4.0) replaces the dashboard with native tables, removes the 1,000-domain session split, and separates queued work from persistent timestamped events. Build `39423924911`, source `bfce41d35454dd46707667f1c8dfec18c88f4169`: all 56 core tests passed on each OS, including one 510,288-domain synthetic session; protected WPF smoke passed. The Windows integration test timeout was increased after the initial three-minute disk-flush deadline expired; the final complete run passed. ZIP: 1,634,516 bytes, SHA-256 `62037ea11509c6d17f5ee0c37b6298f0de4f9c3dfc8fb4ccb1f79268b50584fa`. Publisher workflow `39424695744` staged release `418389099`; all three downloaded asset hashes, payload evidence, source and release notes matched before owner publication. [Evidence](security/v0.4.0-wpf-evidence.json). Prior endpoint investigation and its limitations remain unresolved.

## 0.4.1 missing cached RDAP route fix publication

[v0.4.1 Preview](https://github.com/bisrib613/moz-checker/releases/tag/v0.4.1) refreshes official IANA bootstrap data when a cached TLD route is missing, including `.fr`. Build `39440577252`, source `4ee0b9861b4e307618e7ef08728a9ed0d4ced934`: 63 core tests passed on each OS and protected WPF smoke passed. ZIP: 1,635,389 bytes, SHA-256 `63fb6f1b2d0a04e25aa7d808a2bd11e4b405a7a5b2563e8c49a84243415f3d92`. Publisher `39441401247` staged release `418508274`; downloaded asset hashes, payload evidence, source and exact release notes were independently verified before owner publication. [Evidence](security/v0.4.1-wpf-evidence.json). Earlier Python endpoint investigation remains unresolved; the global block on unreviewed packages remains enabled.

## 0.5.0 reviewed workspace and process continuation publication

[v0.5.0 Preview](https://github.com/bisrib613/moz-checker/releases/tag/v0.5.0) publishes the code/workflow review, checkpoint-based resume, scoped retry, configurable availability workers and revised WPF workspace. Build `39514795641`, source `06c3f264b7f736d4d86fe578f09ca841d826dee8`: 92 core tests passed on Windows and Linux, 9 publisher tests passed, and protected WPF smoke passed with persisted resume UI plus regular/compact screenshots. The 57 archived Python source tests passed separately on Linux; no Python executable was rebuilt. ZIP: 1,644,392 bytes; SHA-256 `da08e8b1cafcafc54524e2b763552cc834d8da4a86c6a61e6a462b7dbfbca42c`. Publisher `39515663688` staged release `418983169`; downloaded metadata, all three asset hashes and protected payload evidence matched before owner publication. [Evidence](security/v0.5.0-wpf-evidence.json); [review and limitations](CODE-REVIEW-0.5.0.md). Earlier endpoint investigation and the default block for unreviewed packages remain unresolved/enabled.
