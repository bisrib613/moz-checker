# C# / WPF migration (0.3.0 development)

Windows 11 x64 now has a C# implementation in `dotnet/`, targeting .NET 10 LTS and native WPF. The app package contains no Python interpreter, PyInstaller, Qt, web server, remote UI fonts, or executable unpacker. The legacy Python source and tests are kept as a reference during migration; they are not part of the new Windows package.

The build is **framework-dependent**: install Microsoft's **.NET 10 Desktop Runtime x64** separately. Windows 11 does not guarantee it is preinstalled. This reduces the download by sharing the installed .NET runtime; it is not a claim that C# alone makes every executable smaller. The whole extracted folder is needed. No single-file extraction, compression packer, obfuscation, elevated privileges, or automatic installer is used.

## Compatibility

- Same SQLite file `%LOCALAPPDATA%/MozChecker/checker.sqlite3`, tables, snake_case JSON, per-source timestamps, and quota accounting. The first .NET open backs up an existing database through SQLite's backup API to `checker.sqlite3.before-dotnet.bak`, including committed WAL data. The backup is never overwritten automatically.
- Existing `MozChecker` / `username@MozChecker` generic credentials are read using Win32 Credential Manager APIs and UTF-16, matching the Python keyring backend's actual convention. There is no plaintext credential copy or fallback. Saving an empty value removes that key. Credential migration needs manual Windows validation with an existing user profile; CI never uses a user's real keys.
- Close Python first. A residual Python `desktop.lock` blocks .NET opening instead of racing database writes. A separate exclusive file handle prevents a second .NET instance. All interrupted/queued jobs are paused on launch; choose Lanjutkan explicitly.
- Registered, redemption, pending_delete, unknown, and queued records appear in the log. Only available candidates enter the main grid and CSV. Availability requires a recognized registrable domain, authoritative IANA RDAP protocol 404, and DNS NXDOMAIN. Plain HTTP 404 is insufficient. Unsupported TLDs remain unknown; WHOIS is not implemented.
- OPR remains separately labeled, cached for 30 days and requested in batches of at most 100. Availability cache is 15 minutes. Bootstrap is cached 7 days. Wayback samples up to 20 snapshots (default 5), caps metadata at 10,000 unique HTML digests, and ignores script/style/noscript/template text. Only metadata/verdict/evidence are stored; snapshot HTML is not rendered or persisted.
- Availability uses at most 8 workers. Bounded channels feed independent serial Wayback and Moz lanes. Rate limits and Retry-After apply. Canceled work is persisted and resumable. Source failures stay attached to the affected result.
- Moz remains **legacy Links API v2** with access ID + secret, not a v3 token. Only fresh available domains with valid/no_history verdicts qualify. Atomic local reservation precedes the request; failed/interrupted billable calls never retry automatically. An existing reserved request becomes uncertain. Quota is local, not fetched from the provider.
- Search, TLD/history/length/OPR/DA/snapshot filters, column sorting, reset, CSV formula neutralization, evidence dialogs, matched snapshot browser links, and keyboard shortcuts are retained. The native grid is virtualized, and dialogs/sidebar scroll.

## Validation and remaining limits

Core tests exercise normalization, PSL exceptions, Python-compatible JSON/SQLite, backup, availability evidence, stale availability refresh before Moz, source failure isolation, no duplicate billing, quota races, cancellation/resume, CSV, and a 1,000-domain pipeline. Native WPF smoke uses a fresh temporary profile with simulated rows, checks the available/log separation, search/reset and export state, and requests normal/minimum window sizes. The runner display can constrain the actual window bounds; its captured render is 1040×768, so a full 1440×900 display and high-DPI layout remain manual checks. Fixtures are never inserted into real user data.

The Windows build is scanned before startup. Real-time and behavior protection must be active, applicable runner exclusions removed only on the disposable GitHub VM, firewall profiles enabled, and outbound traffic blocked for the smoke executable. Scan/startup evidence is retained. These checks do **not** replicate the user's Windows 11 cloud ML behavior, establish a false positive, or resolve the previous detection. `release-security.json` remains blocked and publication must fail.

Paid provider integration with real API keys, representative end-to-end live hunting, screen-reader behavior, high DPI and existing-user Credential Manager migration remain manual validation work. No public 0.3.0 release is authorized as a security-cleared replacement by this migration.

## Developer commands

Use .NET 10 SDK (`global.json` permits stable feature updates):

```sh
dotnet restore dotnet/MozChecker.Tests --locked-mode
dotnet test dotnet/MozChecker.Tests --no-restore --configuration Release
dotnet restore dotnet/MozChecker.Desktop --locked-mode
dotnet build dotnet/MozChecker.Desktop --no-restore --configuration Release
```

Windows package: `pwsh ./scripts/build-windows.ps1`. Native UI cannot run on Linux; run the protected validation workflow on a disposable Windows runner. SDK/package cache paths in cloud setup use writable workspace directories.

The embedded ICANN Public Suffix List comes from the same MPL-2.0 snapshot bundled by the existing tldextract dependency; see `dotnet/MozChecker.Core/Data/NOTICE.txt`. Package lock files pin transitive dependencies. NuGet security audit remains enabled; known vulnerable dependencies are not suppressed.

Native WPF empty workspace captured from the protected Windows smoke run (no real domain data):

![Native WPF workspace](desktop-wpf.png)
