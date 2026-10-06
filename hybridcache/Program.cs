using System.Collections.Concurrent;
using System.ComponentModel;
using System.Diagnostics;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;
using Microsoft.Extensions.Caching.Distributed;
using Microsoft.Extensions.Caching.Hybrid;
using Microsoft.Extensions.Caching.Memory;
using Microsoft.Extensions.DependencyInjection;

// Soru: Popüler bir cache anahtarı boşken (ya da süresi dolduğunda) aynı anda çok istek gelirse
// veritabanına kaç kez gidilir? IMemoryCache, HybridCache ve elle yazılmış tek-uçuş (single-flight) kilidi.
//
//   dotnet run -c Release -- soguk <istek>          aynı anahtara eşzamanlı istek, cache boş
//   dotnet run -c Release -- anahtarlar <anahtar> <istek/anahtar>
//   dotnet run -c Release -- sure <kullanici> <saniye>   sürekli trafik, 1 sn ömürlü kayıt
//   dotnet run -c Release -- iki <yerel-omur-sn>    iki sunucu, ortak L2: silme diğer sunucuya ulaşıyor mu?
//   dotnet run -c Release -- isabet                 BenchmarkDotNet: cache'te olan kaydı okumanın bedeli

switch (args)
{
    case ["soguk", var n]:
        await Senaryo.Soguk(int.Parse(n));
        return;
    case ["anahtarlar", var k, var n]:
        await Senaryo.Anahtarlar(int.Parse(k), int.Parse(n));
        return;
    case ["sure", var u, var s]:
        await Senaryo.SureDolmasi(int.Parse(u), int.Parse(s));
        return;
    case ["iki", var omur]:
        await IkiSunucu.Calistir(int.Parse(omur));
        return;
    case ["isabet", .. var rest]:
        BenchmarkRunner.Run<IsabetMaliyeti>(args: rest);
        return;
    default:
        Console.WriteLine("Kullanım: soguk <istek> | anahtarlar <anahtar> <istek> | sure <kullanici> <saniye> | iki <yerel-omur-sn> | isabet");
        return;
}

// Sahte veritabanı: her çağrıyı sayar, 50 ms sürer. Aynı anda en fazla 100 sorgu çalışır
// (SqlClient ve Npgsql'de varsayılan Max Pool Size 100); fazlası bağlantı bekler.
public sealed class SahteDb
{
    private readonly SemaphoreSlim _havuz = new(100);
    private int _cagri;
    public int Cagri => _cagri;
    public static readonly TimeSpan Gecikme = TimeSpan.FromMilliseconds(50);

    public async Task<Urun> UrunGetirAsync(string anahtar, CancellationToken ct = default)
    {
        Interlocked.Increment(ref _cagri);
        await _havuz.WaitAsync(ct);
        try { await Task.Delay(Gecikme, ct); }
        finally { _havuz.Release(); }
        return new Urun(anahtar, "Örnek ürün", 199.90m);
    }
}

public sealed record Urun(string Id, string Ad, decimal Fiyat);

// Okuma yolları aynı arayüzün arkasında: anahtar + "yoksa veritabanından getir".
public interface IOkuyucu
{
    string Ad { get; }
    ValueTask<Urun> GetirAsync(string anahtar, TimeSpan omur);
}

public sealed class MemoryCacheOkuyucu(SahteDb db) : IOkuyucu
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    public string Ad => "IMemoryCache.GetOrCreateAsync";

    public async ValueTask<Urun> GetirAsync(string anahtar, TimeSpan omur) =>
        (await _cache.GetOrCreateAsync(anahtar, e =>
        {
            e.AbsoluteExpirationRelativeToNow = omur;
            return db.UrunGetirAsync(anahtar);
        }))!;
}

public sealed class HybridCacheOkuyucu : IOkuyucu
{
    private readonly HybridCache _cache;
    private readonly SahteDb _db;
    public string Ad => "HybridCache (yalnızca L1)";

    public HybridCacheOkuyucu(SahteDb db)
    {
        _db = db;
        var services = new ServiceCollection();
        services.AddHybridCache();
        _cache = services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }

    public ValueTask<Urun> GetirAsync(string anahtar, TimeSpan omur) =>
        _cache.GetOrCreateAsync(
            anahtar,
            async ct => await _db.UrunGetirAsync(anahtar, ct),
            new HybridCacheEntryOptions { Expiration = omur, LocalCacheExpiration = omur });
}

// Elle çözüm: aynı anahtar için yalnızca bir Task başlat, diğerleri onu beklesin.
public sealed class TekUcusOkuyucu(SahteDb db) : IOkuyucu
{
    private readonly MemoryCache _cache = new(new MemoryCacheOptions());
    private readonly ConcurrentDictionary<string, Lazy<Task<Urun>>> _ucustakiler = new();
    public string Ad => "IMemoryCache + elle tek-uçuş";

    public async ValueTask<Urun> GetirAsync(string anahtar, TimeSpan omur)
    {
        if (_cache.TryGetValue(anahtar, out Urun? urun))
            return urun!;

        var lazy = _ucustakiler.GetOrAdd(anahtar, a => new Lazy<Task<Urun>>(async () =>
        {
            try
            {
                var u = await db.UrunGetirAsync(a);
                _cache.Set(a, u, omur);
                return u;
            }
            finally
            {
                _ucustakiler.TryRemove(a, out _);
            }
        }));
        return await lazy.Value;
    }
}

public static class Senaryo
{
    private static IEnumerable<(IOkuyucu okuyucu, SahteDb db)> Okuyucular()
    {
        var d1 = new SahteDb(); yield return (new MemoryCacheOkuyucu(d1), d1);
        var d2 = new SahteDb(); yield return (new HybridCacheOkuyucu(d2), d2);
        var d3 = new SahteDb(); yield return (new TekUcusOkuyucu(d3), d3);
    }

    private static async Task Isit()
    {
        // JIT ve thread pool ısınması; ölçüme karışmasın.
        foreach (var (o, _) in Okuyucular())
            await Task.WhenAll(Enumerable.Range(0, 50).Select(i => o.GetirAsync($"isinma-{i % 3}", TimeSpan.FromSeconds(1)).AsTask()));
        ThreadPool.GetMinThreads(out var w, out var io);
        ThreadPool.SetMinThreads(Math.Max(w, 64), io);
    }

    public static async Task Soguk(int istek)
    {
        await Isit();
        Console.WriteLine($"## Soğuk anahtar: {istek} eşzamanlı istek, aynı anahtar, veritabanı 50 ms\n");
        Console.WriteLine("| Yöntem | Veritabanı çağrısı | Toplam süre | Medyan | p99 |");
        Console.WriteLine("|---|---:|---:|---:|---:|");
        foreach (var (o, db) in Okuyucular())
        {
            var sureler = new double[istek];
            var toplam = Stopwatch.StartNew();
            using var basla = new ManualResetEventSlim();
            var gorevler = Enumerable.Range(0, istek).Select(i => Task.Run(async () =>
            {
                basla.Wait();
                var sw = Stopwatch.StartNew();
                await o.GetirAsync("urun:42", TimeSpan.FromMinutes(5));
                sureler[i] = sw.Elapsed.TotalMilliseconds;
            })).ToArray();
            toplam.Restart();
            basla.Set();
            await Task.WhenAll(gorevler);
            toplam.Stop();
            Array.Sort(sureler);
            Console.WriteLine($"| {o.Ad} | {db.Cagri} | {toplam.Elapsed.TotalMilliseconds:F0} ms | {sureler[istek / 2]:F0} ms | {sureler[(int)(istek * 0.99)]:F0} ms |");
        }
    }

    public static async Task Anahtarlar(int anahtar, int istekBasina)
    {
        await Isit();
        Console.WriteLine($"## {anahtar} anahtar × {istekBasina} eşzamanlı istek (cache boş)\n");
        Console.WriteLine("| Yöntem | Veritabanı çağrısı | İdeal | Toplam süre |");
        Console.WriteLine("|---|---:|---:|---:|");
        foreach (var (o, db) in Okuyucular())
        {
            using var basla = new ManualResetEventSlim();
            var gorevler = Enumerable.Range(0, anahtar * istekBasina).Select(i => Task.Run(async () =>
            {
                basla.Wait();
                await o.GetirAsync($"urun:{i % anahtar}", TimeSpan.FromMinutes(5));
            })).ToArray();
            var sw = Stopwatch.StartNew();
            basla.Set();
            await Task.WhenAll(gorevler);
            Console.WriteLine($"| {o.Ad} | {db.Cagri} | {anahtar} | {sw.Elapsed.TotalMilliseconds:F0} ms |");
        }
    }

    public static async Task SureDolmasi(int kullanici, int saniye)
    {
        await Isit();
        var omur = TimeSpan.FromSeconds(1);
        Console.WriteLine($"## Sürekli trafik: {kullanici} kullanıcı, {saniye} sn, kayıt ömrü 1 sn, her kullanıcı istekler arası 10 ms bekliyor\n");
        Console.WriteLine("| Yöntem | Toplam istek | Veritabanı çağrısı | İdeal (≈ saniye) | Çağrı / süre dolması |");
        Console.WriteLine("|---|---:|---:|---:|---:|");
        foreach (var (o, db) in Okuyucular())
        {
            long istek = 0;
            using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(saniye));
            var gorevler = Enumerable.Range(0, kullanici).Select(_ => Task.Run(async () =>
            {
                while (!cts.IsCancellationRequested)
                {
                    await o.GetirAsync("urun:42", omur);
                    Interlocked.Increment(ref istek);
                    try { await Task.Delay(10, cts.Token); } catch (OperationCanceledException) { }
                }
            })).ToArray();
            await Task.WhenAll(gorevler);
            Console.WriteLine($"| {o.Ad} | {istek:N0} | {db.Cagri} | {saniye} | {(double)db.Cagri / saniye:F1} |");
        }
    }
}

// Cache'te olan bir kaydı okumak ne kadar sürer?
// HybridCache L1'de değiştirilebilir (mutable) tipleri her okumada yeniden deserialize eder; sealed + [ImmutableObject(true)]
// tiplerde aynı nesneyi döner. Bu farkı da görmek için iki tip var.
[MemoryDiagnoser]
public class IsabetMaliyeti
{
    public sealed class DegisebilirUrun { public string Id { get; set; } = ""; public string Ad { get; set; } = ""; public decimal Fiyat { get; set; } }

    [ImmutableObject(true)]
    public sealed record DegismezUrun(string Id, string Ad, decimal Fiyat);

    private MemoryCache _memory = null!;
    private HybridCache _hybrid = null!;
    private static readonly HybridCacheEntryOptions Secenek = new() { Expiration = TimeSpan.FromHours(1), LocalCacheExpiration = TimeSpan.FromHours(1) };

    [GlobalSetup]
    public async Task Kur()
    {
        _memory = new MemoryCache(new MemoryCacheOptions());
        _memory.Set("u", new DegisebilirUrun { Id = "u", Ad = "Örnek ürün", Fiyat = 199.90m });
        var services = new ServiceCollection();
        services.AddHybridCache();
        _hybrid = services.BuildServiceProvider().GetRequiredService<HybridCache>();
        await _hybrid.SetAsync("m", new DegisebilirUrun { Id = "m", Ad = "Örnek ürün", Fiyat = 199.90m }, Secenek);
        await _hybrid.SetAsync("i", new DegismezUrun("i", "Örnek ürün", 199.90m), Secenek);
    }

    [Benchmark(Baseline = true)]
    public DegisebilirUrun? MemoryCache_TryGetValue() =>
        _memory.TryGetValue("u", out DegisebilirUrun? u) ? u : null;

    [Benchmark]
    public ValueTask<DegisebilirUrun> HybridCache_Degisebilir() =>
        _hybrid.GetOrCreateAsync("m", _ => ValueTask.FromResult(new DegisebilirUrun()), Secenek);

    [Benchmark]
    public ValueTask<DegismezUrun> HybridCache_Degismez() =>
        _hybrid.GetOrCreateAsync("i", _ => ValueTask.FromResult(new DegismezUrun("", "", 0)), Secenek);
}

// İki sunucu: iki ayrı HybridCache örneği (her birinin kendi L1'i), Redis yerine ortak tek bir IDistributedCache (L2).
// Banka listesi değişir, A kaydı siler; B eski listeyi ne kadar süre gösterir?
public static class IkiSunucu
{
    private static int _surum = 1;
    private static int _sorgu;

    private static Task<string> ListeGetirAsync() { Interlocked.Increment(ref _sorgu); return Task.FromResult($"liste v{_surum}"); }

    private static HybridCache Sunucu(IDistributedCache ortakL2)
    {
        var services = new ServiceCollection();
        services.AddSingleton(ortakL2);
        services.AddHybridCache();
        return services.BuildServiceProvider().GetRequiredService<HybridCache>();
    }

    public static async Task Calistir(int yerelOmurSn)
    {
        Console.WriteLine($"## İki sunucu, ortak L2; L1 ömrü {yerelOmurSn} sn, L2 ömrü 1 saat\n");
        Console.WriteLine("| Silme yöntemi | A silmeden hemen sonra | B silmeden hemen sonra | B'nin güncel listeyi görmesi |");
        Console.WriteLine("|---|---|---|---:|");
        foreach (var yontem in new[] { "RemoveAsync(anahtar)", "RemoveByTagAsync(tag)" })
        {
            _surum = 1;
            var l2 = new MemoryDistributedCache(Microsoft.Extensions.Options.Options.Create(new MemoryDistributedCacheOptions()));
            HybridCache a = Sunucu(l2), b = Sunucu(l2);
            var secenek = new HybridCacheEntryOptions { Expiration = TimeSpan.FromHours(1), LocalCacheExpiration = TimeSpan.FromSeconds(yerelOmurSn) };
            string[] tag = ["bankalar"];
            ValueTask<string> Oku(HybridCache c) => c.GetOrCreateAsync("banka-listesi", async _ => await ListeGetirAsync(), secenek, tag);

            await Oku(a); await Oku(b);              // iki sunucu da v1'i önbellekledi
            _surum = 2;                               // veritabanında liste değişti
            if (yontem.StartsWith("RemoveAsync")) await a.RemoveAsync("banka-listesi");
            else await a.RemoveByTagAsync("bankalar");

            var aHemen = await Oku(a);
            var bHemen = await Oku(b);
            var sw = Stopwatch.StartNew();
            string bSon = bHemen;
            while (bSon != "liste v2" && sw.Elapsed < TimeSpan.FromSeconds(yerelOmurSn * 3 + 5))
            {
                await Task.Delay(100);
                bSon = await Oku(b);
            }
            var sonuc = bSon == "liste v2" ? $"{sw.Elapsed.TotalSeconds:F1} sn sonra" : "görmedi";
            Console.WriteLine($"| {yontem} | {aHemen} | {bHemen} | {sonuc} |");
        }

        // Karşılaştırma: TTL'siz IMemoryCache (yazıdaki ilk durum). Silme yalnızca A'da.
        var ma = new MemoryCache(new MemoryCacheOptions());
        var mb = new MemoryCache(new MemoryCacheOptions());
        _surum = 1;
        foreach (var m in new[] { ma, mb }) await m.GetOrCreateAsync("banka-listesi", _ => ListeGetirAsync());
        _surum = 2;
        ma.Remove("banka-listesi");
        var maS = await ma.GetOrCreateAsync("banka-listesi", _ => ListeGetirAsync());
        var mbS = await mb.GetOrCreateAsync("banka-listesi", _ => ListeGetirAsync());
        Console.WriteLine($"| IMemoryCache, TTL yok, Remove | {maS} | {mbS} | süresiz eski |");
    }
}
