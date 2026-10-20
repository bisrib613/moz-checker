# PRD Expired Domain Checker

Oct 19, 2026 · @beck

## 1. Ringkasan & latar belakang

Expired Domain Checker memeriksa daftar domain, memastikan mana yang benar-benar available atau hampir dilepas registry, lalu menampilkan metrik otoritas (Moz DA/PA) supaya pengguna bisa memilih domain bekas yang layak dibeli.

**Masalah.** Mencari expired domain berkualitas dilakukan manual: cek ketersediaan di satu tool, cek DA di tool lain, cek riwayat di tool ketiga. Metrik Moz mahal untuk volume besar, dan kuota gratisnya sangat kecil, sehingga banyak domain terbuang sia-sia untuk dicek.

**Pendekatan produk.** Pipeline berlapis: filter murah dan gratis dulu (ketersediaan, skor otoritas gratis, riwayat arsip), lalu pakai kuota Moz hanya untuk domain finalis yang masih available. Dengan begitu data DA/PA Moz tetap bisa dipakai tanpa biaya untuk penggunaan skala kecil.

**Platform.** Aplikasi desktop untuk Windows. Semua pengecekan berjalan dari komputer pengguna tanpa server pusat: antrean job, cache, dan hasil disimpan lokal, dan API key tidak keluar dari perangkat kecuali ke penyedia API-nya sendiri; kunci disimpan di Windows Credential Manager.

**Versi dokumen.** Draf 1.0, dibuat 19 Oktober 2026. Angka kuota dan harga pihak ketiga perlu diverifikasi ulang di halaman resmi sebelum implementasi (lihat bagian 3).

## 2. Tujuan, non-tujuan, dan pengguna

Produk dianggap berhasil jika pengguna bisa memasukkan ratusan domain dan mendapat shortlist domain available berkualitas dalam satu sesi, tanpa biaya API berbayar.

**Tujuan**

- Memeriksa ketersediaan dan status kedaluwarsa (available, redemption, pending delete) secara massal.
- Menampilkan Moz DA, PA, dan Spam Score untuk domain finalis.
- Menyediakan skor otoritas gratis sebagai pengganti sementara saat kuota Moz habis.
- Menyaring domain yang pernah memuat judi atau porn lewat riwayat Wayback Machine.
- Ekspor hasil ke CSV.

**Non-tujuan (MVP)**

- Tidak membeli atau mendaftarkan domain otomatis.
- Tidak meng-scrape situs Moz atau tool SEO lain yang melarangnya di syarat layanannya.
- Tidak menjanjikan data DA untuk setiap domain; Moz tidak selalu punya data untuk domain yang sudah lama mati.
- Tidak menyediakan backlink analysis lengkap.

**Pengguna**

| Persona | Kebutuhan utama |
| --- | --- |
| Praktisi SEO / pembangun blog jaringan sendiri | Mencari domain bekas ber-otoritas untuk proyek baru |
| Flipper domain | Menyaring cepat daftar drop harian, memilih yang bernilai jual |
| Pemilik bisnis kecil | Mengecek apakah domain incaran bisa dibeli dan seberapa bersih riwayatnya |

## 3. Sumber data: Moz DA/PA dan opsi gratis

Moz DA/PA bisa diambil gratis, tetapi hanya sekitar 50 domain per bulan lewat API; untuk volume lebih besar, pakai Open PageRank (gratis, 30.000 domain/bulan) sebagai penyaring dan simpan kuota Moz untuk finalis.

| Sumber | Data yang didapat | Batas gratis | Catatan |
| --- | --- | --- | --- |
| [Moz Links API](https://growthmethod.com/moz-data-mcp-server/) | DA, PA, Spam Score | 50 rows/bulan, 1 request per 10 detik ([sumber](https://help.link-assistant.com/hc/en-us/articles/5190144128796-How-do-I-check-Page-Authority-and-Domain-Authority-using-a-MOZ-API-key-)) | Token gratis dilaporkan butuh kartu kredit; kuota dihitung per row, bukan per request. Berbayar mulai $20/bulan untuk 3.000 rows ([sumber](https://thatmarketingbuddy.com/api/moz)) |
| Moz Link Explorer (web) | DA, PA, Spam Score, link count | 10 query/bulan dengan akun gratis | Manual lewat browser, tidak untuk otomasi |
| [Open PageRank](https://keywordseverywhere.com/openpagerank/pricing) | Skor otoritas 0–10 dari Common Crawl | 30.000 domain/bulan, 60 request/menit, tanpa kartu | Bukan DA; skala berbeda dan tidak boleh dicampur dengan DA di tabel yang sama. Domain baru atau baru berpindah tangan bisa terbaca rendah ([metodologi](https://keywordseverywhere.com/openpagerank/methodology)) |
| RDAP via [IANA bootstrap](https://data.iana.org/rdap/dns.json) | Ketersediaan, status (redemption, pending delete), tanggal kedaluwarsa | Gratis, tanpa API key | Respons 404 bisa berarti TLD tidak punya RDAP, bukan domain available ([penjelasan](https://dev.to/glitchbound/whois-is-gone-rdap-replaced-it-and-a-404-does-not-mean-what-you-think-3alh)) |
| DNS (NXDOMAIN) | Konfirmasi tambahan ketersediaan | Gratis | Dipakai bersama RDAP, tidak berdiri sendiri |
| Wayback Machine (Availability + CDX) | Riwayat situs, jumlah snapshot | Gratis; wrapper komunitas memakai batas konservatif 15 request/menit ([sumber](https://github.com/Mearman/mcp-wayback-machine)) | CDX kadang 503/429; Availability kadang kosong untuk URL yang sebenarnya diarsipkan ([sumber](https://dataimpulse.com/blog/wayback-machine-api/)) |

**Keputusan produk**

1. Aturan available: domain dinyatakan available hanya jika RDAP (atau WHOIS untuk TLD tanpa RDAP) dan DNS sama-sama menunjukkan tidak terdaftar. Selain itu statusnya *unknown*, bukan available.
2. Kolom skor selalu berlabel sumbernya ("Moz DA" atau "OPR"). Jika Moz belum diambil untuk domain tersebut, kolom DA kosong dan OPR tetap tampil.
3. Kuota Moz dipakai hanya untuk domain valid, begitu domain itu ditemukan, dan berhenti saat sisa kuota mencapai cadangan. Penghitung sisa kuota tampil di layar.
4. Pengguna memasukkan API key Moz sendiri (BYO key); kunci disimpan di penyimpanan aman sistem operasi dan hanya dikirim ke penyedia API yang bersangkutan.
5. Moz tidak selalu punya data untuk domain yang tidak lagi aktif; tampilkan "tidak ada data", jangan diisi 0.

*Per 19 Oktober 2026. Angka Moz berasal dari sumber sekunder dan harus dicek di halaman harga resmi Moz sebelum implementasi.*

## 4. Kebutuhan fungsional

MVP mencakup input massal, cek ketersediaan, skor gratis, riwayat arsip, DA/PA Moz sesuai permintaan, filter, dan ekspor CSV.

| ID | Kebutuhan | Kriteria penerimaan | Prioritas |
| --- | --- | --- | --- |
| F1 | Input domain: tempel daftar, unggah CSV/TXT, atau generate dari kata kunci + daftar TLD | Maksimal 1.000 domain per job; domain dinormalisasi (huruf kecil, tanpa skema/www, IDN ke punycode); duplikat dibuang dan jumlahnya dilaporkan | P0 |
| F2 | Cek ketersediaan lewat RDAP + DNS | Status salah satu dari: available, registered, redemption, pending delete, unknown; setiap status menyertakan bukti (kode HTTP RDAP, hasil DNS) | P0 |
| F3 | Tanggal kedaluwarsa dan registrar | Tampil bila RDAP mengembalikan event expiration; kosong bila tidak ada | P0 |
| F4 | Skor otoritas gratis (Open PageRank) | Dikirim 100 domain per request; hasil di-cache 30 hari; skor 0–10 dengan label "OPR" | P0 |
| F5 | Validasi riwayat Wayback untuk domain available | Mengambil daftar snapshot (CDX), memindai teks sampel snapshot untuk kata kunci judi dan porn, lalu menetapkan valid, invalid, atau tidak bisa dicek (kriteria di bawah); hanya dijalankan untuk domain lolos F2 | P0 |
| F6 | Moz DA/PA/Spam Score untuk domain valid | Berjalan otomatis begitu sebuah domain dinyatakan valid; berhenti saat sisa kuota mencapai cadangan (default 5 rows, bisa diatur); sisa kuota tampil; permintaan Moz berjalan serial | P0 |
| F7 | Pengaturan API key (Moz, Open PageRank) | Kunci disimpan di penyimpanan aman sistem operasi, bisa diganti atau dihapus; aplikasi tetap jalan tanpa kunci (hanya F2 dan F5) | P0 |
| F8 | Filter dan urutkan | Filter: status, TLD, panjang nama, OPR minimum, DA minimum, jumlah snapshot; urut berdasarkan kolom mana pun | P1 |
| F9 | Ekspor CSV | Semua kolom yang tampil, termasuk sumber dan waktu pengecekan setiap metrik | P0 |
| F10 | Pantau domain (watchlist) | Cek ulang domain berstatus redemption/pending delete tiap hari dan beri notifikasi saat berubah jadi available | P2 |
| F11 | Deteksi tanda spam di riwayat | Tandai domain dengan Spam Score tinggi atau lonjakan snapshot tidak wajar sebagai "berisiko" | P2 |

**Kriteria valid dan invalid (Wayback)**

Domain valid jika Wayback bisa dicek dan tidak ada riwayat judi atau porn; domain invalid jika ada riwayat tersebut.

| Status | Syarat |
| --- | --- |
| Valid | Available, Wayback berhasil dicek, dan tidak ada kata kunci judi atau porn pada snapshot yang dipindai |
| Valid, tanpa riwayat | Available dan Wayback tidak punya snapshot sama sekali; diperlakukan valid dan diberi label "tanpa riwayat" (default, bisa diubah di pengaturan) |
| Invalid | Ada kata kunci judi atau porn pada salah satu snapshot yang dipindai |
| Tidak bisa dicek | Wayback gagal (429, 503, timeout, atau situs dikecualikan dari arsip); tidak dianggap valid dan bisa dicoba ulang |

**Cara deteksi.** CDX hanya memberi metadata snapshot (waktu, kode status, tipe konten, digest), bukan isi halaman. Jadi aplikasi memilih sampel snapshot tersebar dari yang paling awal sampai paling akhir (default maksimal 5 snapshot, bisa diatur), mengunduh isinya sebagai teks saja, lalu mencocokkan judul dan teks dengan daftar kata kunci judi dan porn berbahasa Indonesia dan Inggris. Daftar kata kunci bisa diedit pengguna.

**Batasan.** Pemindaian kata kunci bisa meleset (domain bersih terblokir, atau halaman bermasalah lolos). Karena itu hasil menampilkan kata kunci yang cocok dan tautan ke snapshot-nya agar pengguna bisa memeriksa sendiri. Isi snapshot tidak pernah ditampilkan di aplikasi.

**Catatan perilaku**

- Job berjalan di proses latar belakang aplikasi; jika aplikasi ditutup, job dijeda dan dilanjutkan dari domain terakhir saat aplikasi dibuka lagi (progres disimpan lokal).
- Setiap hasil menyimpan waktu pengecekan; hasil ketersediaan dianggap basi setelah 15 menit dan dicek ulang sebelum ditampilkan sebagai "siap beli".
- Hasil tidak pernah ditulis sebagai "pasti bisa dibeli"; ketersediaan bisa berubah sesaat setelah pengecekan.

## 5. Alur kerja dan arsitektur

Pipeline berjalan dari filter paling murah ke paling mahal, sehingga kuota Moz hanya terpakai untuk domain yang sudah lolos dan dipilih pengguna.

&#91;embedded content: alur pipeline · 6 tahap, 2 titik keluar\]

Domain yang registered atau unknown berhenti di tahap 2, domain di bawah ambang OPR disembunyikan di tahap 3, dan domain berriwayat judi atau porn dinyatakan invalid di tahap 4, jadi hanya domain valid yang bisa memakai kuota Moz di tahap 5.

**Komponen**

- **Antarmuka desktop:** input, progres job, tabel hasil, pengaturan API key.
- **Layanan latar belakang lokal dan antrean job:** satu worker per sumber data, masing-masing dengan token bucket sesuai batas sumbernya.
- **Cache per sumber:** hasil RDAP "not found" 15 menit, OPR 30 hari; durasi cache Wayback ditentukan saat implementasi.
- **Penyimpanan lokal:** data job dan hasil di database berkas pada komputer pengguna; API key di penyimpanan aman sistem operasi.
- **Daftar bootstrap IANA:** diunduh dan diperbarui berkala agar rute RDAP per TLD tetap benar.

## 6. Non-fungsional, UI, dan metrik

**Kebutuhan non-fungsional**

| Aspek | Target |
| --- | --- |
| Kecepatan | 1.000 domain selesai cek ketersediaan + OPR dalam kurang dari 10 menit |
| Keandalan | Satu sumber gagal tidak menggagalkan job; kolom terkait ditandai "gagal, coba lagi" |
| Kepatuhan batas | Antrean dengan token bucket per sumber; hormati header Retry-After pada respons 429 |
| Keamanan | API key disimpan di penyimpanan aman sistem operasi, tidak muncul di log, dan hanya dikirim ke penyedia API-nya |
| Privasi | Daftar domain pengguna tidak dibagikan; cache hanya menyimpan hasil metrik publik |
| Akurasi | Status "available" hanya dengan bukti ganda (RDAP + DNS); selain itu "unknown" |

**Concurrency**

Satu hunting berjalan pada satu waktu; hunting yang dimulai saat ada hunting aktif masuk antrean dan jalan otomatis setelah yang aktif selesai. Di dalam satu hunting, tiap domain mengalir lewat tahapnya dan hasilnya langsung tampil begitu selesai, tanpa menunggu seluruh daftar.

| Tahap | Cara berjalan |
| --- | --- |
| Cek ketersediaan dan OPR | Banyak domain sekaligus; OPR dikirim 100 domain per request, di bawah 60 request per menit |
| Wayback | Satu domain per giliran sesuai batas laju (mulai 15 request per menit, disetel setelah diuji); ini tahap paling lambat |
| Moz | Begitu sebuah domain dinyatakan valid, domain itu masuk antrean Moz dan diproses satu per satu (1 request per 10 detik); berhenti saat sisa kuota mencapai cadangan |

Semua sumber menghormati Retry-After pada respons 429 dan memakai backoff saat gagal. Hunting yang dijeda atau terputus dilanjutkan dari domain terakhir yang selesai.

**Layar utama**

1. Input: kotak tempel domain, tombol unggah, pilihan TLD, tombol Mulai.
2. Progres job: bilah kemajuan per tahap (ketersediaan, skor, riwayat) dan jumlah domain tersisa.
3. Tabel hasil: kolom domain, status, kedaluwarsa, OPR, DA, PA, Spam Score, snapshot; badge warna untuk status; tombol "Ambil DA/PA" per baris dan massal.
4. Pengaturan: API key dan indikator sisa kuota Moz.

**Metrik keberhasilan**

| Metrik | Target 90 hari setelah rilis |
| --- | --- |
| Akurasi status available (dicek manual pada sampel 100 domain) | 98% atau lebih |
| Domain yang dicek per sesi (median) | 200 atau lebih |
| Pemakaian kuota Moz per domain finalis | Tidak ada domain non-available yang memakai kuota Moz |
| Job selesai tanpa error sumber | 95% atau lebih |

## 7. Risiko, tahapan rilis, dan pertanyaan terbuka

Risiko terbesar adalah salah menyatakan domain available dan kuota Moz yang terlalu kecil; keduanya ditangani lewat aturan bukti ganda dan funnel finalis.

**Risiko**

| Risiko | Dampak | Mitigasi |
| --- | --- | --- |
| RDAP 404 untuk TLD tanpa RDAP dibaca sebagai available | Pengguna mengejar domain yang sudah dimiliki orang lain | Cocokkan TLD dengan daftar IANA; TLD tanpa RDAP memakai WHOIS dan DNS, atau berstatus unknown |
| Kuota gratis Moz (50 rows/bulan) habis cepat | Fitur DA tidak berguna untuk volume besar | Moz hanya untuk finalis; OPR sebagai pengganti berlabel; dukung API key berbayar milik pengguna |
| Syarat Moz berubah (kartu kredit, kuota, harga) | Rencana gratis tidak berlaku lagi | Verifikasi di halaman resmi sebelum rilis; lapisan sumber data dibuat dapat diganti |
| Pembatasan laju RDAP, DNS, dan Wayback | Job lambat atau gagal | Antrean per sumber, backoff, cache hasil |
| DA tinggi tetapi riwayat spam | Pengguna membeli domain yang sudah ditandai buruk | Tampilkan riwayat arsip dan Spam Score; label "berisiko" (F11) |
| Penyalahgunaan alat untuk menargetkan merek orang lain | Risiko hukum dan reputasi | Peringatan merek dagang di layar hasil; tidak ada fitur backorder otomatis di MVP |

**Tahapan rilis**

1. **MVP:** F1–F7, F9. Cek ketersediaan, OPR, validasi Wayback, Moz otomatis dengan batas kuota, ekspor CSV.
2. **V1.1:** F8. Filter lengkap.
3. **V2:** F10 dan F11. Watchlist harian, notifikasi, deteksi domain berisiko.

**Pertanyaan terbuka**

- [ ] Versi Windows minimum (Windows 10 atau 11) dan framework aplikasi yang dipakai? Ini menentukan cara penyimpanan lokal dan pembaruan aplikasi.
- [ ] Apakah daftar domain berasal dari input manual saja, atau perlu impor daftar drop harian dari pihak ketiga?
- [ ] TLD apa yang diprioritaskan (.com, .net, .org, .id, .co.id)? TLD tertentu, seperti .id, mungkin punya aturan RDAP/WHOIS berbeda dan perlu diuji.
- [ ] Berapa volume domain per hari yang ditargetkan? Jika di atas 30.000 per bulan, kuota gratis Open PageRank tidak cukup.