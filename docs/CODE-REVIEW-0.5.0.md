# Review kode dan alur kerja 0.5.0

Review mencakup seluruh modul aplikasi C#/WPF aktif (`Core`, `Desktop`, tes), sumber Python lama sebagai referensi kompatibilitas, skrip build/audit/publisher, workflow GitHub Actions, konfigurasi paket terkunci dan alur data SQLite/Credential Manager. Ini review fungsional dan ketahanan proses, bukan sertifikasi keamanan atau jaminan bebas bug.

## Temuan yang diperbaiki

| Temuan | Perubahan | Verifikasi |
| --- | --- | --- |
| Resume selalu masuk RDAP/OPR meski sudah sampai tahap lanjutan | Routing dari checkpoint `opr`, `history`, `moz` beserta varian antreannya | Restart nyata SQLite; enam checkpoint; hasil selesai tetap identik |
| Resume satu cakupan bisa mengerjakan job lain yang masih queued | Engine menerima cakupan sesi eksplisit | Sesi lain tetap queued dan tidak dipanggil providernya |
| Retry mengulang seluruh jalur gratis dan tidak mengambil pekerjaan `no_key`/kuota yang sudah bisa dilanjutkan | Pilih tahap dari sumber gagal; buka kembali no-key/kuota tanpa mengirim ulang permintaan Moz ambigu | Provider tracking tiap domain; failed/reserved/uncertain Moz tidak diulang |
| Penghitung kuota dan marker paid request disimpan terpisah | Satu transaksi SQLite untuk kuota, marker dan event Moz | Restart setelah reserve, penolakan reserve ganda, kuota habis tidak membuat marker |
| Status completed yang tidak konsisten dapat menyembunyikan pekerjaan tertunda | Startup menjadikannya paused bila masih ada baris belum selesai | Fixture selesai + pending; progres dipertahankan |
| Timeout HTTP berhenti mencakup permintaan setelah header diterima | Deadline hingga seluruh body dibaca; pembatalan pengguna tetap dibedakan | Stream macet setelah HTTP 200; paid request tidak retry; caller cancellation |
| Angka API yang berbentuk string bisa menjadi kosong | Parse numerik invariant dengan batas/finite check | Angka teks, nol, NaN, negatif dan di luar rentang |
| Bootstrap IANA gagal pada awal proses bisa diunduh berulang untuk tiap domain | Cooldown juga berlaku saat belum ada bootstrap yang valid | Delapan domain gagal, satu unduhan |
| Cache JSON rusak dapat menghentikan worker | Cache rusak diperlakukan sebagai cache miss | Data hasil/job tidak dihapus atau direset |
| Kegagalan ekspor dapat merusak file CSV sebelumnya | Tulis file sementara dalam folder tujuan, ganti setelah selesai | Reader gagal sesudah satu baris; file lama identik dan temporary dibersihkan |
| Jalur UI resume sulit ditemukan dan input kosong terlalu dominan | Banner jumlah domain tersisa, tombol utama Lanjutkan, input tertutup untuk workspace lama | Native WPF smoke untuk resume dan layout lebar/kompak |
| Beberapa operasi masih menunggu SQLite di UI dan pesan hilang di panel tertutup | Resume/retry/kuota dibaca di background; status operasi tampil di footer; paging diperbaiki saat hasil menyusut | Smoke UI dan regression suite |
| Concurrency tidak dapat diatur | RDAP/DNS 1–32, default 8, tersimpan pada aturan | Tes batas in-flight 1/3, persistensi setting dan input invalid |
| Folder publish bisa menyisakan DLL/lisensi lama | Bersihkan hanya direktori paket hasil build sebelum publish | Build Windows paket baru dan hash seluruh payload |
| Tes tools rilis hanya berjalan saat publikasi | Job CI khusus publisher; rilis mensyaratkan bukti smoke resume | 9 tes publisher, termasuk menolak smoke tanpa resume |

Tema baru menggunakan permukaan putih, latar slate, aksen biru, kontrol berjarak konsisten, hierarki aksi, fokus keyboard yang terlihat dan tabel virtual. Panduan katalog produk umum tidak cocok untuk alat ini; pilihan komposisi/palet dibuat khusus untuk workspace, sedangkan rekomendasi WPF yang terverifikasi digunakan untuk keyboard, Grid, dan virtualisasi.

## Melanjutkan data sebelum update

1. Tutup aplikasi lama dan ekstrak seluruh ZIP versi baru ke folder lain.
2. Buka aplikasi dengan akun Windows dan `MOZ_CHECKER_DATA_DIR` yang sama jika memakai override. Data normal berada di `%LOCALAPPDATA%/MozChecker/checker.sqlite3`.
3. Pilih **Semua pemeriksaan** untuk seluruh antrean lama atau satu pemeriksaan tertentu.
4. Klik banner **Lanjutkan pemeriksaan**. Input tidak perlu diisi; data selesai tidak diulang. Tahap yang terputus diselesaikan dari checkpoint.
5. **Ulang sumber gagal** berbeda dari resume: menjadwalkan ulang sumber gagal/no-key/kuota yang memenuhi syarat. Moz yang berpotensi ditagih tidak dikirim ulang.

Riwayat hasil dan kuota dipertahankan. Event sebelum versi yang menyediakan log persisten tidak dibuat-buat. Status available yang sudah lebih dari 15 menit tetap ditandai basi; validasi registrar tetap diperlukan, dan sebelum permintaan Moz berbayar aplikasi memeriksa ulang ketersediaan yang basi.

## Batas verifikasi

- Tes lokal: 92 tes core, 9 tes tools publisher, 57 tes sumber Python referensi; cross-build WPF tanpa warning/error. Hasil final native Windows dan hash paket dicatat pada bukti rilis.
- Tes 510.288 domain memverifikasi impor/penyimpanan/query/progres, bukan 510.288 permintaan ke provider nyata. Pipeline mock melewati 1.000 domain; batas rate provider tetap berlaku.
- OPR/Moz berbayar dengan akun nyata tidak dipanggil dalam review ini. Integrasi Moz tetap Links API v2, bukan token v3; WHOIS belum tersedia. Gangguan jaringan dan TLD tanpa rute resmi tetap unknown.
- High-DPI, pembaca layar dan perangkat Windows pengguna belum disertifikasi. Screenshot runner menggunakan data sintetis.
- Python 0.2.0 tetap referensi lama, tidak dikemas dalam EXE baru. Tes sumber lama yang lulus tidak menyelesaikan investigasi karantina binary lamanya. Keterbatasan investigasi dicatat di SECURITY-INVESTIGATION.md.
