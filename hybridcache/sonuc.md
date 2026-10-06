# HybridCache ve cache stampede: ölçüm sonuçları (2026-10-06)

Yazı: https://huseyinbozdemir.com/blog/imemorycache-hybridcache (9 Ekim 2026'da yayında)

.NET 10.0.1, Windows 11, AMD Ryzen 7 5700X (8 çekirdek, 16 mantıksal). Paketler: Microsoft.Extensions.Caching.Hybrid 10.10.0,
Caching.Memory 10.0.12, BenchmarkDotNet 0.15.8. HybridCache yalnızca L1 (Redis yok).

Sahte veritabanı: her sorgu 50 ms, aynı anda en fazla 100 sorgu (SqlClient/Npgsql varsayılan Max Pool Size 100),
fazlası bağlantı bekler. Her senaryo tek tur; tekrar edilmedi.

Üç yöntem: `IMemoryCache.GetOrCreateAsync`, `HybridCache.GetOrCreateAsync`, `IMemoryCache` + elle tek-uçuş
(`ConcurrentDictionary<string, Lazy<Task<T>>>`, ~20 satır).

## 1. Soğuk anahtar: aynı anahtara eşzamanlı istek (`soguk <n>`)

| İstek | Yöntem | Veritabanı çağrısı | Toplam süre | Medyan | p99 |
|---:|---|---:|---:|---:|---:|
| 100 | IMemoryCache | 100 | 63 ms | 61 ms | 62 ms |
| 100 | HybridCache | 1 | 61 ms | 60 ms | 60 ms |
| 100 | Elle tek-uçuş | 1 | 52 ms | 52 ms | 52 ms |
| 1.000 | IMemoryCache | 1.000 | 614 ms | 366 ms | 612 ms |
| 1.000 | HybridCache | 1 | 54 ms | 53 ms | 53 ms |
| 1.000 | Elle tek-uçuş | 1 | 61 ms | 61 ms | 61 ms |
| 5.000 | IMemoryCache | 5.000 | 2.856 ms | 1.506 ms | 2.853 ms |
| 5.000 | HybridCache | 1 | 65 ms | 62 ms | 64 ms |
| 5.000 | Elle tek-uçuş | 1 | 57 ms | 56 ms | 56 ms |

Havuz sınırı olmadan (ilk deneme) süreler üç yöntemde de ~55 ms çıktı; çağrı sayıları aynıydı. Yani asıl zarar havuz
dolunca başlıyor: 100 isteğe kadar fark görünmüyor, sonra gecikme doğrusal artıyor.

## 2. Çok anahtar: 100 anahtar × 10 eşzamanlı istek (`anahtarlar 100 10`)

| Yöntem | Veritabanı çağrısı | İdeal | Toplam süre |
|---|---:|---:|---:|
| IMemoryCache | 1.000 | 100 | 567 ms |
| HybridCache | 100 | 100 | 64 ms |
| Elle tek-uçuş | 100 | 100 | 54 ms |

HybridCache birleştirmeyi anahtar başına yapıyor; farklı anahtarlar birbirini beklemiyor.

## 3. Sürekli trafik, süre dolması (`sure 200 10`)

200 kullanıcı, 10 sn, her kullanıcı istekler arasında 10 ms bekliyor; kayıt ömrü 1 sn (yani ~10 kez doluyor).

| Yöntem | Toplam istek | Veritabanı çağrısı | Süre dolması başına |
|---|---:|---:|---:|
| IMemoryCache | 118.198 | 1.800 | 180 |
| HybridCache | 121.461 | 10 | 1 |
| Elle tek-uçuş | 121.196 | 10 | 1 |

Gerçek hayattaki stampede bu: kayıt her dolduğunda o 50 ms'lik pencerede gelen herkes veritabanına gidiyor.

## 4. Cache isabeti: kayıt zaten cache'teyken okumanın bedeli (`isabet`, BenchmarkDotNet)

| Yöntem | Süre | Ayrılan bellek |
|---|---:|---:|
| `IMemoryCache.TryGetValue` | 28,4 ns | 0 |
| HybridCache, değiştirilebilir sınıf (`{ get; set; }`) | 519,7 ns (18,3×) | 120 B |
| HybridCache, `sealed` + `[ImmutableObject(true)]` record | 42,6 ns (1,5×) | 0 |

Şaşırtıcı kısım: HybridCache değiştirilebilir (mutable) tipleri L1'de bile her okumada yeniden deserialize ediyor
(çağıran nesneyi değiştirirse cache bozulmasın diye). Tip `sealed` ve `[ImmutableObject(true)]` ise aynı nesneyi döndürüyor.
Bunu bilmeyen biri IMemoryCache'ten geçince her isabette ~18 kat yavaşlama ve bellek ayırma alır.
Kaynak (doğrulandı): https://learn.microsoft.com/en-us/aspnet/core/performance/caching/hybrid, "Reuse objects" bölümü.
Aynı sayfa: stampede koordinasyonu yalnızca aynı HybridCache örneğinde; anahtar/tag ile silme diğer sunucuların L1'ine ulaşmıyor.

## 5. İki sunucu, ortak L2: silme diğer sunucuya ulaşıyor mu? (`iki <L1-ömrü-sn>`)

İki ayrı HybridCache örneği (A, B; her birinin kendi L1'i), Redis yerine ortak tek `MemoryDistributedCache` (L2, ömür 1 saat).
İkisi de "banka listesi v1"i okur; veritabanında liste v2 olur; A kaydı siler; sonra B her 100 ms'de okur.

| L1 ömrü | Silme yöntemi | A hemen sonra | B hemen sonra | B'nin v2'yi görmesi |
|---:|---|---|---|---:|
| 5 sn | `RemoveAsync(anahtar)` | v2 | v1 | 5,0 sn sonra |
| 5 sn | `RemoveByTagAsync(tag)` | v2 | v1 | 5,0 sn sonra |
| 30 sn | `RemoveAsync(anahtar)` | v2 | v1 | 30,1 sn sonra |
| 30 sn | `RemoveByTagAsync(tag)` | v2 | v1 | 30,1 sn sonra |
| — | `IMemoryCache`, TTL yok, `Remove` | v2 | v1 | süresiz eski |

B, L1 ömrü dolana kadar eski listeyi gösteriyor; tag ile silmek de fark etmiyor. Dokümandaki notla aynı.
Sunucular tek süreçte simüle edildi; gerçek Redis yok ama HybridCache L2'ye yalnızca `IDistributedCache` üzerinden eriştiği için sonuç değişmez.

## Zayıf yanlar

- Veritabanı simülasyon (`Task.Delay` + semafor); gerçek PostgreSQL/SQL Server yok. Gerçek veritabanında 1.000 eşzamanlı
  sorgu CPU/kilit yüzünden daha da kötü olur; burada yalnızca havuz beklemesi var.
- Redis (L2) ölçülmedi. Çok sunuculu kurulumda her sunucunun L1'i ayrı; stampede sunucu sayısı kadar çağrıya iner.
- Senaryolar tek tur; sayılar (çağrı sayıları) deterministik, süreler makine yüküne göre oynar.
