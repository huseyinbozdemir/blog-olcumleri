# Blog ölçümleri

[huseyinbozdemir.com](https://huseyinbozdemir.com) blogundaki ölçüme dayalı yazıların kodu ve ham sonuçları.
Rakamları kendi makinende yeniden üretebilmen için.

| Klasör | Yazı | Ne ölçülüyor |
|---|---|---|
| [exception-maliyeti](exception-maliyeti) | [C#'ta exception pahalı mı? .NET 10 ölçümleri](https://huseyinbozdemir.com/blog/csharp-exception-performansi) | try/catch, throw + catch, int.Parse vs TryParse |
| [hybridcache](hybridcache) | [IMemoryCache mi, HybridCache mi?](https://huseyinbozdemir.com/blog/imemorycache-hybridcache) | Cache stampede, iki sunucuda silme, cache isabet maliyeti |

## Çalıştırma

.NET 10 SDK gerekir. Her klasör bağımsız bir konsol projesi.

```bash
cd exception-maliyeti
dotnet run -c Release                     # BenchmarkDotNet, hangi ölçümün çalışacağını sorar

cd hybridcache
dotnet run -c Release -- soguk 1000       # aynı anahtara 1.000 eşzamanlı istek
dotnet run -c Release -- sure 200 10      # sürekli trafik, 1 sn ömürlü kayıt
dotnet run -c Release -- iki 30           # iki sunucu: silme diğerine ulaşıyor mu?
dotnet run -c Release -- isabet           # BenchmarkDotNet: cache isabet maliyeti
```

Her klasördeki `sonuc.md` yazıdaki rakamların alındığı ham çıktı ve ölçüm ortamı (işlemci, .NET sürümü).
Süreler makineye göre değişir; oranlar ve sorgu sayıları değişmemeli.
