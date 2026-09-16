# SQL izleme — canlı monitör

.NET 8 üzerinde çalışan, SQL Server instance'larını canlı izleyen web uygulaması.
Bu sürüm yalnızca **Canlı İzleme** ekranını kapsıyor.

---

## Kurulum

### 1. İzleme veritabanı — elle bir şey yapmana GEREK YOK

Uygulama her açılışta `Monitor:StoreConnectionString`'in gösterdiği
veritabanını (varsayılan ad: `SqlMonitorDb`) kontrol eder, yoksa
kendisi oluşturur - şemayı, tabloları, saklı yordamları. Zaten varsa
hiçbir şeye dokunmaz (tamamen idempotent), yalnızca `İzleme veritabanı
... hazır` diye loglar.

Bağlantı dizesinde `Database=` ne yazıyorsa o ad kullanılır - istediğin
adı verebilirsin. Kurulumdan sonra uygulama o veritabanını kendi
bağlantı dizesiyle gerçekten açabildiğini de doğrular; açamazsa tek ve
açık bir hata satırı basar (sessizce bozuk bir kuruluma düşmez).

Kurulum betiği artık ayrı bir dosya olarak yok - `Sql/StoreSchemaSql.cs`
içine gömülü, uygulamanın kendisiyle birlikte taşınıyor.

Bu, izleme verisini tutmak istediğin sunucu için geçerli - izlediğin
prod sunucu **olmak zorunda değil**, ayrı bir sunucu daha iyidir (prod
çöktüğünde geçmiş veriye bakabilmek için).

### 2. İzleme hesabını aç

`db/02_monitoring_login.sql` dosyasını **izlenecek** sunucuda çalıştır.
Çalıştırmadan önce içindeki şifreyi değiştir.

Bu betik `sysadmin` vermiyor, bilerek. Uygulama yalnızca okuyor;
`VIEW SERVER STATE` + `VIEW ANY DEFINITION` yeterli. Bağlantı dizesinde
sysadmin taşımak, ileride uygulamada çıkacak herhangi bir açığı doğrudan
sunucunun tamamına çevirir.

### 3. `appsettings.json` dosyasını doldur (yalnızca İLK açılış için)

```json
"Monitor": {
  "StoreConnectionString": "Server=localhost;Database=SqlMonitorDb;Integrated Security=true;TrustServerCertificate=true",
  "CollectIntervalSeconds": 30,
  "Instances": [
    {
      "Name": "TEST",
      "DisplayName": "Test",
      "ConnectionString": "Server=10.0.0.10;Database=master;User Id=sqlmonitor_ro;Password=...;TrustServerCertificate=true",
      "IsDefault": true,
      "RequiresLogin": false
    },
    {
      "Name": "PROD",
      "DisplayName": "Üretim SQL",
      "ConnectionString": "Server=10.0.0.20;Database=master;TrustServerCertificate=true",
      "IsDefault": false,
      "RequiresLogin": true
    }
  ]
}
```

`StoreConnectionString` (`Server=localhost;...;Integrated Security=true`) yerel
makinenin **varsayılan** (adsız) SQL Server örneğine, çalıştıran
kişinin Windows hesabıyla bağlanır - başka bir makinede çalıştırırken
elle değiştirmen gerekmez. Adlandırılmış bir örnek kullanıyorsan
(örn. SQLEXPRESS) `Server=localhost\SQLEXPRESS` yap; SQL Server o
makinede hiç yoksa uzaktaki bir sunucuyu göstermen gerekir.

**`Instances` dizisi yalnızca İLK AÇILIŞTA, izleme veritabanı
(`mon.MonitoredInstance` tablosu) BOŞSA bir tohum olarak okunur.**
Uygulama bir kere açıldıktan sonra sunucu listesi tamamen ekrandaki
**"⚙ Sunucular"** düğmesinden yönetilir - ekle/düzenle/sil, hiçbiri
için `appsettings.json`'u değiştirip uygulamayı yeniden başlatman
gerekmez. Bu dosyadaki `Instances`'ı bir daha hiç düzenlemesen de olur;
yalnızca sıfırdan bir kurulumun ilk açılışında işe yarar.

**`RequiresLogin: true` olan bir instance'ın `ConnectionString`'inde
`User Id`/`Password` OLMAMALI** - kullanıcı adı/şifre appsettings.json'a
hiç yazılmaz, uygulama açıldığında ekrandan sorulur ve gerçek bir
bağlantı denemesiyle doğrulanır (bkz. `Api/AuthEndpoints.cs`,
`Services/ProdCredentialStore.cs`). `RequiresLogin: false` (ya da hiç
yazılmamışsa varsayılan) olan bir instance içinse normal şekilde
`User Id`/`Password` doğrudan bağlantı dizesine yazılır - o instance
uygulama açılır açılmaz otomatik bağlanır, hiçbir şey sormaz. Bu ikisi
ekrandaki "Sunucuları yönet" formunda da aynen var (bir onay kutusu:
"Şifreyi burada saklama, ekrandan sor").

Kullanıcı adını **boş bırakırsan Windows kimlik doğrulaması** kullanılır -
yerel/etki alanı içindeki bir sunucuyu ayrı bir SQL hesabı açmadan
izlemek için yeterli.

### 4. Slack bildirimi (isteğe bağlı)

```json
"Monitor": {
  "SlackWebhookUrl": "https://hooks.slack.com/services/..."
}
```

Boş bırakılırsa bildirim özelliği tamamen kapalıdır (kod webhook'a
hiç dokunmaz) - ayrı bir aç/kapa ayarı yok.

> **Webhook'u `appsettings.json`'a YAZMA.** O dosya depoya giriyor;
> webhook'u eline geçiren herkes kanalına mesaj atabilir. Gerçek değeri
> `appsettings.Local.json` içine koy - bu dosya `.gitignore`'da, yalnızca
> senin makinende durur ve `appsettings.json`'ı ezer:
>
> ```json
> { "Monitor": { "SlackWebhookUrl": "https://hooks.slack.com/services/..." } }
> ```
>
> Aynı dosyayı kendi bağlantı dizen gibi başka yerel ayarlar için de
> kullanabilirsin. Sunucuya kurarken ortam değişkeni de olur:
> `Monitor__SlackWebhookUrl=...`

Webhook tek ve geneldir; **hangi sunucunun** bildirim göndereceği ise
sunucu başına seçilir: "Sunucuları yönet" formundaki **"Critical
durumları Slack'e bildir"** kutucuğu. Varsayılan kapalı - yeni eklenen
bir sunucu kendiliğinden bildirim göndermeye başlamaz.

Ne zaman mesaj gider:

- Bir sağlık kontrolü **Critical**'e düştüğünde (kırmızı) ve
  **düzeldiğinde** (yeşil). `Warning` seviyesi Slack'e gitmez, yalnızca
  ekranda görünür.
- Critical'e düşmek tek başına yetmez: kontrolün **arka arkaya 2
  toplayıcı turu** (varsayılan aralıkla ~1 dakika) Critical kalması
  gerekir - bkz. `Services/SlackDebouncer.cs`. Eşiğin tam sınırında
  gidip gelen bir metrik (flapping) böylece kanal doldurmaz.
- Süregelen bir sorun için **tek** mesaj gider, her turda tekrarlanmaz.
  "Düzeldi" mesajı da yalnızca gerçekten alarm verilmiş bir dönem için
  gönderilir.

Eşikleri `Monitor:Thresholds` altından kendi sunucunun normaline göre
ayarla - hangi kontrolün neye baktığı `Services/HealthEvaluator.cs`
içinde.

### 5. Çalıştır

```
dotnet restore
dotnet run
```

Tarayıcıda `http://localhost:5000` (veya konsolda yazan port).

---

## Mimari

```
Tarayıcı
   │  her N saniyede GET /api/live/snapshot
   ▼
LiveEndpoints ──► LiveMonitorService ──► [izlenen SQL Server]
                        │                    DMV'ler
                        ├──► HealthEvaluator      (eşik → hüküm → skor)
                        └──► MetricStore ──► [SqlMonitorDb]  (trend, olay)

CollectorService (arka plan, tarayıcıdan bağımsız)
   └──► her 30 sn ──► aynı okuma ──► MetricStore'a yaz
```

Ekranın **canlı** kısmı doğrudan DMV'lerden okunur, hiçbir yere yazılmadan.
**Zamana bağlı** kısmı (trend çizgileri, olay çizelgesi, bekleme farkı)
`SqlMonitorDb`'den gelir ve onu `CollectorService` doldurur.

Bu ayrım önemli: uygulama arka planda kapalıyken de canlı ekran çalışır,
sadece geçmiş boş görünür.

---

## Neden bazı şeyler böyle yapıldı

**Kümülatif sayaçlar.** SQL Server'ın `/sec` sayaçları aslında saniyelik
değil, açılıştan beri artan toplamlardır. `838.019.669` gibi bir sayı
görürsün ve hiçbir anlamı yoktur. `CounterDeltaTracker` iki okuma
arasındaki farkı alıp süreye böler. İlk okumada `—` gösterir — uydurma
bir sıfır göstermektense bilmediğini söylemek daha dürüst.

**Bekleme istatistikleri.** `sys.dm_os_wait_stats` de kümülatiftir.
45 gün uptime'lı bir sunucuda tepedeki bekleme tipi, 45 günün toplamıdır;
şu an olan biteni anlatmaz. Bu yüzden `mon.WaitSample` tablosunda ham
değerler saklanır ve ekranda iki örnek arasındaki fark gösterilir.
Toplayıcı henüz ikinci örneği almadıysa ekran bunu açıkça yazar.

**Dosya I/O gecikmeleri.** Bunlar da kümülatif. Ekranda "446 ms yazma
gecikmesi" görmek diskin **şu an** yavaş olduğu anlamına gelmez.
Panelin altındaki not bunu hatırlatıyor. Bir sonraki fazda delta hesabı
buraya da eklenebilir.

**Panel bazlı hata yalıtımı.** Her panel kendi `try/catch`'i içinde.
Bir DMV yetki hatası verdiğinde ekranın tamamı değil, sadece o panel
kaybolur ve üstte hangi panelin patladığı yazar. Kırılgan bir izleme
aracına kimse güvenmez.

**Plan XML'i asla toplu çekilmez.** `sys.dm_exec_query_plan` pahalıdır.
Toplu listede plan çekmek, prod'da ciddi CPU yakan klasik bir hatadır.
Burada plan yalnızca bir satıra tıklandığında, tek oturum için,
ayrı bir uçtan gelir.

**Kısa timeout.** İzlenen sunucuya açılan her komut 8 saniyeyle sınırlı.
Sunucu zorda olduğunda izleme aracının onu daha da bekletmesi kabul
edilemez; panel boş kalır, sunucu rahat kalır.

---

## Dosya haritası

| Yol | Ne yapar |
|---|---|
| `Program.cs` | DI kayıtları, pipeline |
| `Options/MonitorOptions.cs` | `appsettings.json` karşılığı, **eşikler burada** |
| `Sql/DmvSql.cs` | Bütün DMV sorguları — sunucuya ne gönderdiğini burada görürsün |
| `Services/LiveMonitorService.cs` | DMV'leri okur, ekranın modelini kurar |
| `Services/HealthEvaluator.cs` | Ham metriği hükme ve 0-100 skora çevirir |
| `Services/CounterDeltaTracker.cs` | Kümülatif sayaç → saniyelik oran |
| `Services/MetricStore.cs` | `SqlMonitorDb` okuma/yazma |
| `Services/CollectorService.cs` | Arka plan toplayıcı |
| `Api/LiveEndpoints.cs` | REST uçları |
| `wwwroot/` | Arayüz (framework yok, derleme adımı yok) |
| `db/` | Şema ve yetki betikleri |

---

## API

| Uç | Ne döner |
|---|---|
| `GET /api/instances` | Tanımlı instance listesi |
| `GET /api/live/snapshot?instance=X` | Ekranın tamamı, tek JSON |
| `GET /api/live/timeline?instance=X&hours=24` | Durum değişikliği olayları |
| `GET /api/live/session/{spid}/plan?instance=X` | Tek sorgunun metni ve planı |
| `POST /api/live/kill-session` | Bir oturumu sonlandırır (`{instance, sessionId}`) |
| `GET /api/live/missing-indexes?instance=X` | Eksik index önerileri |
| `GET /api/live/top-procedures?instance=X&sort=cpu` | En ağır prosedürler |
| `GET /api/live/unused-procedures?instance=X` | Kullanıldığına dair kanıt bulunmayan prosedürler |
| `GET /api/live/alwayson?instance=X` | Always On grup/replika/veritabanı sağlığı |
| `GET /api/live/agent-jobs?instance=X` | SQL Agent job'larının son durumu |
| `GET /api/live/wait-trend?instance=X&hours=6` | Bekleme türlerinin zaman içi trendi |
| `GET /api/live/errorlog?instance=X` | Hata günlüğü (varsayılan kapalı) |
| `GET /api/health` | Uygulamanın kendi sağlığı, toplayıcı yaşıyor mu |
| `GET/POST/PUT/DELETE /api/settings/instances` | Sunucu listesi yönetimi |
| `GET /api/auth/status`, `POST /api/auth/login`, `POST /api/auth/logout` | `RequiresLogin` sunucular için giriş |

---

## Bilinen sınırlar

- **Eşikler kalibre edilmemiş.** `appsettings.json`'daki değerler makul
  başlangıçlar ama senin sunucunun karakterini bilmiyorlar. Birkaç gün
  izleyip kendi normaline göre ayarla — sürekli sarı yanan bir eşik,
  hiç yanmayan bir eşik kadar işe yaramaz.
- **Skor puanlaması keyfi.** Kritik 13, uyarı 4 puan. Bu sayılar
  `HealthEvaluator.BuildPanel` içinde sabit; değiştirmen normal.
- **Disk doluluk tahmini tek hacme bakar.** `disk_used_pct` yalnızca o
  anda EN DOLU hacmi kaydeder; en dolu hacim zaman içinde değişirse
  eğim gürültülenebilir.
- **Kimlik doğrulama yok.** Uygulama açık; sadece localhost'ta veya
  kapalı bir ağda çalıştır. Dışarı açacaksan önce kimlik doğrulama ekle.
- **Hata günlüğü kapalı.** `sp_readerrorlog` `securityadmin` istiyor.

---

## Sonraki adımlar

1. Eşikleri kendi sunucuna göre kalibre et
2. Dosya I/O'suna delta hesabı ekle
3. Kimlik doğrulama (Windows auth en pratiği)
4. Eşik aşımında e-posta/Teams bildirimi
5. Ondan sonra yeni sekmeler: eksik indeks, en pahalı sorgular, job denetimi
