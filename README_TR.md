# Mojave Desktop Radio (Türkçe)

Pip-Boy radyosuna **Mojave Desktop Radio** istasyonunu ekler. İstasyonu açtığınızda bilgisayarınızda
açık olan **Spotify masaüstü uygulaması** çalmaya başlar. Pip-Boy'dan duraklatabilir, parça
değiştirebilir, çalan sanatçı/parçayı görebilir ve müziğe eski radyo sesi verebilirsiniz
(Clean / Mojave / Wasteland / Damaged).

* İndirme, kayıt, DRM yok: kontrol Windows'un medya tuşları altyapısıyla yapılır, efekt RAM'de canlı uygulanır.
* 3 kayıtlık küçük bir ESP, **hiçbir vanilla kaydını ezmez** — mevcut save'e eklenip çıkarılabilir.
* VUI+ ve diğer arayüz modlarının dosyaları değiştirilmez (UIO ile eklenir).
* **Varsayılan olarak hiçbir tuş atanmaz.**

## Gereksinimler

* Fallout: New Vegas 1.4.0.525, xNVSE 6.3+, JIP LN NVSE, ShowOff xNVSE, UIO
* Oyun içi ayarlar için: The Mod Configuration Menu + MCM Extender
* Windows 10 21H2+ / 11, .NET 8 Desktop Runtime (x64), Spotify masaüstü uygulaması
* Ses efekti için (isteğe bağlı): VB-CABLE (ücretsiz, önerilir) veya Steam ile gelen "Steam Streaming Speakers"

Bağlantılar ve ayrıntılar: [README.md](README.md).

## Kurulum

1. MO2/Vortex: `MojaveDesktopRadio-<sürüm>.zip` arşivini normal bir mod gibi kurun, etkinleştirin,
   `MojaveDesktopRadio.esp` işaretli olsun (sıra önemli değil). Elle: arşivi `Data` klasörüne açın.
2. Spotify'ı açın ve **bir parçayı bir kez başlatın** (Spotify, Windows'a ilk çalmadan sonra kaydolur).
3. Oyunu NVSE ile başlatıp save yükleyin. BridgeHost (tepside yeşil radyo simgesi) kendiliğinden açılır,
   oyundan çıkınca birkaç saniye içinde kapanır.
4. Pip-Boy → VERİ → RADYO → **Mojave Desktop Radio**.

Türkçe yama kullanıyorsanız ek ayar gerekmez: metinlerin kod sayfası Windows'tan otomatik alınır
(Türkçe Windows'ta 1254). Pip-Boy yazılarını çevirmek için
`Data\NVSE\Plugins\MojaveDesktopRadio.ini` → `[Text]` bölümünü düzenleyin (dosyayı ANSI/Windows-1254 kaydedin).

## Kullanım

* Paneldeki satırlara fareyle tıklayın: PAUSE/PLAY, NEXT TRACK, PREVIOUS TRACK, CHANGE FX MODE.
* Başka istasyona geçince ya da radyoyu kapatınca Spotify duraklar (MCM'den "sessize al" seçilebilir);
  oyun radyosu ile Spotify aynı anda çalmaz.
* MCM → Mojave Desktop Radio: FX, davranış ve (varsayılanı boş) kısayol ayarları.

## Kaldırma

Tepsi simgesi → **Exit (restores audio routing)**, ardından modu kapatın. Save'ler etkilenmez.

## Sorun giderme

[docs/TROUBLESHOOTING.md](docs/TROUBLESHOOTING.md) (İngilizce, tablo hâlinde). En sık durumlar:

| Belirti | Çözüm |
|---|---|
| `SIGNAL LOST - BRIDGE OFFLINE` | .NET 8 Desktop Runtime kurulu mu? `Data\MojaveDesktopRadio\BridgeHost\MojaveDesktopRadio.exe`'yi elle çalıştırın |
| `STATUS: PRESS PLAY IN SPOTIFY ONCE` | Spotify'da bir kez oynat'a basın |
| `FX MODE: … (NO ROUTE - CLEAN)` | VB-CABLE kurun ya da Steam'in sanal hoparlörünün etkin olduğunu kontrol edin |
| Spotify'ın sesi hiç gelmiyor | Tepsi → Exit. Hâlâ yoksa: Windows Ayarlar → Ses → Ses karıştırıcı → Spotify → Çıkış: Varsayılan |

Günlükler: `<oyun klasörü>\MojaveDesktopRadio.log`, `%LOCALAPPDATA%\MojaveDesktopRadio\logs\bridgehost.log`.
