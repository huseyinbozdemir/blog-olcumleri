using System.Runtime.CompilerServices;
using BenchmarkDotNet.Attributes;
using BenchmarkDotNet.Running;

BenchmarkSwitcher.FromAssembly(typeof(Program).Assembly).Run(args);

// Yaygın inanış: "Exception pahalıdır, asla kullanma."
// Soru 1: Hata hiç olmadığında try/catch bloğunun bir bedeli var mı?
// Soru 2: Hata olduğunda throw + catch, sonucu dönen bir yola göre ne kadar pahalı? Yığın derinliği fark eder mi?
// Soru 3: Gerçek bir örnek: geçersiz girdi int.Parse + catch mi, int.TryParse mı?

[MemoryDiagnoser]
public class HataYokken
{
    private int _deger = 42;

    [Benchmark(Baseline = true)]
    public int TryCatchYok() => Hesapla(_deger);

    [Benchmark]
    public int TryCatchVar()
    {
        try { return Hesapla(_deger); }
        catch (InvalidOperationException) { return -1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Hesapla(int x) => x * 2 + 1;
}

[MemoryDiagnoser]
public class HataOlunca
{
    [Params(1, 10)]
    public int Derinlik { get; set; }

    [Benchmark(Baseline = true)]
    public int SonucDon() => SonucIle(Derinlik).Kod;

    [Benchmark]
    public int FirlatYakala()
    {
        try { return Firlat(Derinlik); }
        catch (InvalidOperationException) { return -1; }
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static Sonuc SonucIle(int derinlik) =>
        derinlik <= 1 ? new Sonuc(false, -1) : SonucIle(derinlik - 1);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int Firlat(int derinlik) =>
        derinlik <= 1 ? throw new InvalidOperationException("Kayıt bulunamadı") : Firlat(derinlik - 1);

    public readonly record struct Sonuc(bool Basarili, int Kod);
}

[MemoryDiagnoser]
public class GecersizGirdi
{
    private readonly string _girdi = "12a4";

    [Benchmark(Baseline = true)]
    public int TryParse() => int.TryParse(_girdi, out var n) ? n : -1;

    [Benchmark]
    public int ParseCatch()
    {
        try { return int.Parse(_girdi); }
        catch (FormatException) { return -1; }
    }
}
