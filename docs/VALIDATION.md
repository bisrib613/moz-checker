# Status validasi

Dibangun dari PRD pengguna pada 20 Oktober 2026. Platform cloud: Python 3.12, Linux tanpa display; target produk Windows 10/11 x64.

## Terverifikasi

- Instalasi dependency menggunakan `uv sync --frozen --extra dev` dan `uv.lock`.
- 53 tes otomatis offline: normalisasi/IDN/Public Suffix List (tidak menyatakan subdomain available), impor/limit 1.000 domain, bukti RDAP+DNS, batching/cache OPR, sampling/temuan arsip, error/retry, kuota lintas koneksi database, pause/resume, pipeline 1.000 domain simulasi, CSV, filter, dan dialog pengaturan.
- Ruff, startup/render Qt offscreen, inisialisasi SQLite, dan shutdown.
- Build sdist/wheel dan executable Linux dengan PyInstaller; executable Linux lolos `--smoke`. Ini tidak membuktikan executable Windows.
- Bootstrap IANA berhasil diambil lewat HTTPX dengan `Accept-Encoding: identity`. Respons terkompresi proxy sempat gagal decoding; permintaan tanpa kompresi mengatasinya tanpa melemahkan TLS verification.
- Live RDAP: example.com, example.net, example.org menghasilkan HTTP 200, status registered, tanggal expiration dan registrar. Ini bukan pengujian domain available.
- Wayback CDX untuk domain dokumentasi `moz-checker-onboarding-test.invalid` menghasilkan 0 snapshot dan status no_history. CDX example.com merespons metadata yang melebihi 10.000 entri; aplikasi menolak menganggap arsip sebesar itu bersih tanpa pemeriksaan.
- Pipeline live ketika sumber gagal menyelesaikan job sebagai unknown dan menyimpan kesalahan; tidak mengisi metrik palsu.

## Belum terbukti / sebelum rilis

- DNS resolver sistem cloud menghasilkan error pada domain live. Tidak ada klaim ketersediaan available yang tervalidasi end-to-end. Uji DNS pada Windows asli atau environment dengan resolver yang diizinkan.
- Akses eksternal bervariasi: urllib/proxy awal memberi 403, sementara beberapa permintaan HTTPX berhasil. Dokumentasi Moz masih gagal melalui proxy; endpoint Moz v2 memberi 503; Open PageRank belum dapat dijangkau. Harga/kuota PRD belum diverifikasi.
- Dokumentasi DomCop merespons 301 ke `https://keywordseverywhere.com/openpagerank/docs`; destination belum dapat diakses. Verifikasi kontrak API dan kuota terkini di sana sebelum rilis.
- Tidak ada API key nyata. OPR dan Moz diuji dengan respons simulasi; adapter Moz v2 memerlukan Access ID + Secret akun yang berhak menggunakan endpoint legacy. Token Moz v3 belum didukung.
- RDAP .id belum berhasil (proxy error). .id/.co.id memakai bootstrap jika tersedia, selain itu unknown. Tidak ada fallback WHOIS.
- Uji pengambilan isi snapshot Wayback live, Windows Credential Manager, dan executable Windows pada Windows asli. Workflow CI menjalankan tes Windows dan smoke test executable; periksa run GitHub Actions pada commit rilis untuk hasilnya. Pengujian manual Windows Credential Manager dan API berkey tetap perlu dilakukan.
- Uji target performa jaringan 1.000 domain, saldo/limit akun API, akurasi sampel available, dan false positive/negative arsip sebelum rilis.

## Jaringan dan konfigurasi

Draft menyimpan install_script, start_skill, dan host HTTPS: data.iana.org, web.archive.org, openpagerank.domcop.com, lsapi.seomoz.com, rdap.verisign.com, rdap.publicinterestregistry.org, moz.com, www.domcop.com, keywordseverywhere.com. Preset package_managers tetap dipertahankan. Host RDAP tambahan harus mengikuti bootstrap IANA untuk TLD yang diperlukan; DNS resolver sistem merupakan jalur terpisah dari HTTPS proxy.

Pengguna meninjau dan menyimpan pengaturan environment, lalu Publish untuk snapshot reusable. Penyimpanan draft tidak membuktikan publikasi atau pemulihan pada task baru. Publikasi source dan aset release dilakukan terpisah melalui GitHub; penyimpanan draft environment tidak mem-publish kode.

API key aplikasi diisi melalui dialog Windows, bukan secret cloud atau .env. Publikasi snapshot environment bukan publikasi aplikasi Windows.
