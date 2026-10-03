# Dokumen PIA Berdasarkan Kode Project: Fungsi, Fitur, dan Arsitektur yang Benar-benar Terimplementasi

## 1. Pengantar

Dokumen ini dibuat berdasarkan kode sumber proyek `Pigeon_Harvest` dan fokus pada komponen yang benar-benar ada di project. Dalam project ini, istilah **PIA** merujuk pada **Pigeon Intelligent Assistant** — asisten AI yang terintegrasi pada Ground Control Station (GCS) untuk drone/UAV.

PIA bukan sekadar label, tetapi fitur nyata yang terhubung ke:
- chat panel UI,
- telemetri drone,
- analisis anomaly,
- prediksi perawatan,
- support LLM dengan fallback provider,
- dan command advice yang aman untuk operator.

Dokumen ini menjelaskan:
1. definisi PIA di project ini,
2. fungsi utamanya,
3. arsitektur kode yang mendukungnya,
4. fitur yang benar-benar tersedia,
5. dan hubungan dengan workflow UAV/GCS.

---

## 1.1 Versi gampang dipahami untuk orang awam

Kalau disederhanakan, PIA itu seperti asisten pintar yang selalu ada di samping pilot drone. Ia tidak hanya melihat layar, tetapi juga membaca data, mengingat kondisi drone, dan memberi saran saat misi sedang berjalan.

Bayangkan Anda sedang mengoperasikan drone di area luas. Ada banyak angka dan indikator: baterai, GPS, ketinggian, kecepatan, dan status sistem. Tanpa bantuan, semua itu bisa membingungkan. PIA membantu mengubah data teknis itu menjadi informasi yang mudah dimengerti.

Dengan kata lain:
> PIA adalah “co-pilot digital” untuk drone yang membantu manusia mengambil keputusan lebih cepat, lebih aman, dan lebih tepat.

Ia bisa menjawab pertanyaan seperti:
- “Status drone saat ini bagaimana?”
- “Baterai sudah cukup aman untuk lanjut?”
- “GPS-nya normal?”
- “Apakah misi masih aman?”

Jadi, PIA itu bukan sekadar fitur bagus, tetapi sistem yang membuat operasi drone terasa lebih cerdas dan lebih aman.

---

## 1.2 Versi yang lebih “wah” dan berkesan

PIA adalah lapisan kecerdasan buatan yang dipasang di atas sistem Ground Control Station (GCS), sehingga drone tidak hanya dikendalikan secara mekanis, tetapi juga dibantu secara inteligensia. Ia berperan sebagai otak pendamping operasional yang terus memantau keadaan drone, menganalisis data real-time, dan menyampaikan insight penting kepada operator sebelum masalah berkembang menjadi bencana.

Dalam konteks modern, PIA adalah kombinasi antara:
- asisten operasional,
- analis telemetri,
- pengawas keamanan misi,
- dan sistem keputusan berbasis AI.

Jadi, bukan hanya alat untuk mengendalikan drone, tetapi sebuah ekosistem pendukung keputusan yang membuat drone tampak lebih cerdas, lebih responsif, dan lebih siap menghadapi situasi kritis.

---

## 2. Definisi PIA dalam project ini

Berdasarkan kode, PIA adalah sistem asisten cerdas yang membantu operator drone dalam:
- memantau status drone,
- mengecek baterai dan GPS,
- menganalisis kondisi misi,
- menangkap anomaly dari telemetri,
- memberi jawaban atau saran berbasis AI,
- dan membantu navigasi keputusan operasional.

PIA diimplementasikan melalui beberapa komponen utama:
- `NaturalLanguageService`
- `ChatViewModel`
- `AISettings`
- `LLMServiceFactory`
- `TelemetryAnalysisService`
- `AnomalyDetectionService`
- `MaintenancePredictionService`
- `BatteryPredictionService`
- `PerformanceScoringService`
- `VoiceCommandService`

---

## 3. Lokasi utama implementasi di kode

Berikut komponen penting yang membentuk PIA:

### 3.1 Chat panel dan UI
- `Pigeon_Harvest/HarvestmoonGCS/ViewModels/ChatViewModel.cs`
- `Pigeon_Harvest/HarvestmoonGCS/Views/Controls/PIAPanel.xaml.cs`
- `Pigeon_Harvest/HarvestmoonGCS/MainPage_Modern.xaml.cs`

Di sini PIA diwakili sebagai panel chat dan tombol AI yang dapat dibuka dari aplikasi.

### 3.2 Natural language / intent processing
- `Pigeon_Harvest/HarvestmoonGCS.Core/Services/AI/NaturalLanguageService.cs`

Ini adalah inti logika PIA: menerima pertanyaan/operator command lalu mengklasifikasikan intent dan menghasilkan jawaban.

### 3.3 Konfigurasi AI
- `Pigeon_Harvest/HarvestmoonGCS.Core/Models/AI/AISettings.cs`

File ini berisi konfigurasi:
- provider AI,
- fallback provider,
- API key,
- base URL,
- model settings,
- telemetry sampling,
- anomaly detection,
- retention history.

### 3.4 LLM provider orchestration
- `Pigeon_Harvest/HarvestmoonGCS.Core/Services/AI/LLMServiceFactory.cs`
- `Pigeon_Harvest/HarvestmoonGCS.Core/Services/AI/ServiceCollectionExtensions.cs`

Di sini sistem memilih primary dan fallback provider AI seperti:
- OpenRouter,
- Gemini,
- OpenAI,
- Grok/XAI.

Hal ini memastikan PIA tidak gagal total bila provider utama down.

### 3.5 Pendaftaran layanan AI di app
- `Pigeon_Harvest/HarvestmoonGCS/App.xaml.cs`

DI container meng-register semua layanan PIA dan integrasi AI saat aplikasi startup.

---

## 4. Fungsi utama PIA dalam project

### 4.1 Fungsi asisten operator drone
PIA menyediakan jawaban untuk pertanyaan operasional yang umum, seperti:
- status lengkap drone,
- cek baterai,
- status GPS,
- analisis keamanan misi,
- takeoff,
- landing,
- RTL,
- ARM/DISARM.

Bukti nyata ada di `NaturalLanguageService.ProcessMessageAsync()` dan `GetQuickCommands()`.

### 4.2 Fungsi monitoring telemetri
PIA mengambil konteks dari `TelemetryBuffer` dan menilai kondisi drone saat ini, misalnya:
- battery level,
- GPS status,
- altitude,
- speed,
- health status,
- dan kondisi sistem.

### 4.3 Fungsi anomaly detection
PIA melakukan deteksi anomali terhadap telemetri dan kondisi drone dengan beberapa layer:
- `RuleBasedDetector`
- `StatisticalDetector`
- `AIAnomalyDetector`

Semua dimasukkan lewat `AnomalyDetectionService`.

### 4.4 Fungsi prediksi maintenance
PIA juga punya kemampuan memperkirakan kebutuhan maintenance berdasarkan data telemetri dan performa.

Komponen:
- `MaintenancePredictionService`
- `PerformanceScoringService`
- `BatteryPredictionService`

### 4.5 Fungsi voice command
PIA juga dapat menerima perintah suara melalui `VoiceCommandService` dan `IVoiceRecognitionService`.

Ini membuat operasi lebih natural untuk operator, meskipun tetap diatur dengan safety-first principle.

### 4.6 Fungsi fallback AI dan resilience
PIA dibuat tahan terhadap kegagalan provider AI. `LLMServiceFactory` menggunakan circuit breaker pattern, yaitu:
- primary service,
- fallback service,
- deteksi kegagalan beruntun,
- timeout otomatis,
- recovery saat jaringan pulih.

---

## 5. Fitur PIA yang benar-benar ada di code

### 5.1 Quick commands
`NaturalLanguageService.GetQuickCommands()` mengembalikan 8 command cepat:
- Status
- Baterai
- GPS
- Analisis
- Takeoff
- Land
- RTL
- ARM

Ini adalah fitur nyata yang terimplementasi.

### 5.2 Chat message pipeline
Flow utama:
1. user memasukkan text,
2. message disimpan ke history,
3. `ClassifyAndRespondAsync()` memeriksa intent,
4. menghasilkan response,
5. response dimasukkan ke history.

### 5.3 Response berbasis telemetry
Untuk query seperti `status lengkap drone` atau `cek baterai`, PIA mengambil data dari `TelemetryBuffer` dan membangun response yang dinamis.

### 5.4 LLM fallback
Sistem tidak mengandalkan satu provider saja. Ia mendukung failover ke provider lain menggunakan `LLMServiceFactory`.

### 5.5 AI settings configurable at runtime
`AISettings` memungkinkan konfigurasi:
- provider,
- fallback,
- API key,
- model,
- anomaly thresholds,
- retention periode,
- sampling rate.

### 5.6 Telemetry sampling
`TelemetrySamplingConfig` mengatur interval sampling, threshold delta, dan event trigger untuk data drone.

### 5.7 Persisted chat history
`SqlitePIAHistoryStore` menyimpan chat history ke SQLite, sehingga history tetap ada antar sesi.

### 5.8 System health and readiness status
`ChatViewModel` memiliki properti seperti:
- `HealthStatus`
- `SystemReadiness`
- `IsTelemetryReady`
- `IsAiReady`
- `IsFallbackReady`

Ini menunjukan bahwa PIA didesain sebagai sistem yang sadar kondisi operasional.

### 5.9 Research export
Pada `ChatViewModel` ada command export report:
- `ExportResearchJsonCommand`
- `ExportResearchCsvCommand`

Ini menunjukkan PIA juga berperan pada analisis report/research output.

---

## 6. Alur kerja PIA secara teknis

### 6.1 Flow dari input user ke output assistant

```text
User input
   ↓
ChatViewModel
   ↓
NaturalLanguageService.ProcessMessageAsync()
   ↓
ClassifyAndRespondAsync()
   ├─ status drone
   ├─ battery
   ├─ gps
   ├─ takeoff/landing
   ├─ ARM/DISARM
   ├─ RTL
   ├─ mission analysis
   └─ LLM fallback response
   ↓
ChatMessage response
   ↓
UI panel shows result
```

### 6.2 Flow AI / telemetry / anomaly

```text
Telemetry stream from drone
   ↓
TelemetryBuffer
   ↓
TelemetryAnalysisService
   ↓
AnomalyDetectionService
   ├─ RuleBasedDetector
   ├─ StatisticalDetector
   └─ AIAnomalyDetector
   ↓
Alert / insight / maintenance / scoring
```

### 6.3 Flow LLM provider decision

```text
LLM request
   ↓
LLMServiceFactory.GetService()
   ↓
Check circuit state
   ├─ Closed → primary
   ├─ Open → fallback
   └─ HalfOpen → recovery check
   ↓
Provider executes request
```

---

## 7. Hubungan PIA dengan workflow UAV/GCS

Dalam konteks sistem UAV/GCS, PIA berperan sebagai lapisan inteligensia di atas operasi drone.

### 7.1 Fase pengumpulan data
Drone mengirim telemetri dan video/camera data ke GCS.

### 7.2 Fase interpretasi
`TelemetryAnalysisService` dan anomaly detection menilai data tersebut.

### 7.3 Fase keputusan
`NaturalLanguageService` menerjemahkan peristiwa menjadi informasi yang mudah dipahami operator.

### 7.4 Fase tindakan
Operator dapat memilih tombol atau command untuk:
- check status,
- ARM,
- LAND,
- RTL,
- atau menilai kondisi misi.

### 7.5 Fase monitoring lanjutan
Maintenance, scoring, dan battery forecast berfungsi untuk menilai apakah misi aman dilanjutkan.

Dengan kata lain, PIA di project ini berada pada posisi:
`data drone → interpretasi → rekomendasi → keputusan operator`

---

## 8. PIA dan konsep SAR / UAV (hubungan konseptual)

Walaupun project ini lebih fokus pada GCS dan asisten AI drone, secara konseptual PIA dapat dipandang sebagai bagian dari ekosistem UAV/SAR, karena ia membantu:
- pemantauan real-time,
- keadaan misi,
- deteksi anomali,
- prioritas tindakan,
- dan pengambilan keputusan cepat.

Namun, yang ada di kode saat ini bukanlah implementasi penuh sistem SAR berbasis pencarian korban dari UAV dalam skala operasi rescue. Yang benar-benar work di project adalah:
- AI assistant untuk GCS,
- analisis telemetri,
- anomaly detection,
- LLM provider orchestration,
- dan voice/command assistance.

---

## 9. Kelebihan PIA yang terimplementasi

1. Terintegrasi langsung dengan GCS
2. Menggunakan telemetri nyata dari drone
3. Memiliki struktur AI provider dengan fallback
4. Memiliki chat assistant yang berfungsi sebagai interface operator
5. Dapat menganalisis status operasional dan keamanan misi
6. Memiliki mechanism detections yang lebih dari sekadar template
7. Menyimpan history dalam SQLite
8. Mampu memakai voice command

---

## 10. Batasan yang perlu dipahami

Dari kode yang ada, PIA belum sepenuhnya menjelma menjadi sistem SAR lengkap seperti:
- automasi pencarian korban,
- object tracking multi-target,
- routing evac otomatis,
- geofencing penuh berdasarkan hasil analitik map detail,
- atau command execution langsung tanpa confirm operator.

Yang tersedia saat ini adalah **PIA asisten intelijen operasional** untuk drone/GCS, bukan autonomous SAR engine full-stack.

---

## 11. Ringkasan singkat

Jika disederhanakan, fungsi PIA dalam project ini adalah:

> PIA adalah asisten AI di Ground Control Station yang menghubungkan data telemetri drone, analisis kondisi misi, dan kebutuhan operator agar keputusan tetap cepat, aman, dan berbasis informasi.

Fungsi utamanya adalah:
- monitoring status drone,
- mengecek baterai dan GPS,
- menilai keamanan misi,
- memberikan insight dari data telemetri,
- mendeteksi anomaly,
- memprediksi maintenance dan performa,
- dan menyederhanakan interaksi operator dengan drone melalui chat dan voice.

---

## 12. Kesimpulan

PIA dalam project `Pigeon_Harvest` adalah fitur yang benar-benar terhubung ke arsitektur aplikasi dan bersifat fungsional. Ia bukan hanya dokumen atau ide, tetapi komponen nyata yang ada di code dan aktif dalam app startup serta dependency injection.

Dari sisi kode, PIA bekerja dalam tiga layer utama:
1. Interface operator (chat panel, quick commands, voice)
2. Intelligence layer (LLM, telemetry analysis, anomaly detection)
3. Operational layer (misi, safety, battery, maintenance, performance)

Jadi, jika Anda ingin dokumentasi yang “berdasarkan kode dan work” maka definisi yang paling akurat adalah:

> PIA = Pigeon Intelligent Assistant, yaitu sistem AI yang membantu operator drone/GCS menganalisis telemetri, menjawab pertanyaan operasional, mendeteksi anomali, dan memberi dukungan keputusan aman dalam operasi misi.

---

## 13. Referensi kode utama

- `Pigeon_Harvest/HarvestmoonGCS.Core/Services/AI/NaturalLanguageService.cs`
- `Pigeon_Harvest/HarvestmoonGCS/ViewModels/ChatViewModel.cs`
- `Pigeon_Harvest/HarvestmoonGCS.Core/Models/AI/AISettings.cs`
- `Pigeon_Harvest/HarvestmoonGCS.Core/Services/AI/LLMServiceFactory.cs`
- `Pigeon_Harvest/HarvestmoonGCS.Core/Services/AI/ServiceCollectionExtensions.cs`
- `Pigeon_Harvest/HarvestmoonGCS/App.xaml.cs`
- `Pigeon_Harvest/HarvestmoonGCS/Views/Controls/PIAPanel.xaml.cs`

Dokumen ini dibuat berdasarkan kode yang ada di repository dan mengikuti struktur aktual aplikasi.
