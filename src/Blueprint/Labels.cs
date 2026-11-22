namespace PulsePC.Blueprint;

/// <summary>
/// Надписи на деталях чертежа (модель процессора, видеокарты, платы, памяти). Берутся из паспорта компьютера,
/// поэтому на любом ПК чертёж подписан его собственным железом. Пустая строка — надпись не рисуется.
/// </summary>
public static class BlueprintLabels
{
    public static string Cpu { get; set; } = "";
    public static string Gpu { get; set; } = "";
    /// <summary>Надпись на кожухе видеокарты: «GEFORCE RTX», «RADEON», «ARC».</summary>
    public static string GpuBrand { get; set; } = "";
    public static string Board { get; set; } = "";
    /// <summary>
    /// Мощность блока питания на наклейке («650 W»). Windows её не знает: надпись — только если человек сам указал мощность
    /// («Система» → «Электричество»). Пусто — наклейка без числа.
    /// </summary>
    public static string Psu { get; set; } = "";

    /// <summary>Надпись для наклейки блока питания из настройки hw.psu.watts.</summary>
    public static string PsuLabel(string saved) =>
        int.TryParse(saved, System.Globalization.NumberStyles.Integer, System.Globalization.CultureInfo.InvariantCulture, out int w) && w > 0 ? w + " W" : "";
    public static string RamType { get; set; } = "DDR";
    /// <summary>Например «3200 MHz  8 GB».</summary>
    public static string RamSpec { get; set; } = "";

    /// <summary>
    /// Надписи установленных планок памяти по порядку слотов («3200 MHz 8 GB»): по ним рисуется столько планок, сколько стоит.
    /// Пусто — состав неизвестен, рисуются две планки с RamSpec.
    /// </summary>
    public static IReadOnlyList<string> RamSticks { get; set; } = Array.Empty<string>();

    /// <summary>
    /// Физические накопители сверху вниз (первый — системный). Пусто — список ещё не прочитан,
    /// тогда рисуется разнесённый вид одного NVMe.
    /// </summary>
    public static IReadOnlyList<DriveSpec> Drives { get; set; } = Array.Empty<DriveSpec>();

    /// <summary>Применить новые надписи: сцены перестраиваются при следующей отрисовке.</summary>
    public static void Apply()
    {
        Scenes.Reset();
        Version++;
        Changed?.Invoke();
    }

    /// <summary>Растёт при каждом <see cref="Apply"/>: вид, пропустивший событие (был вне окна), сверяет его при загрузке.</summary>
    public static int Version { get; private set; }

    public static event Action Changed;
}

/// <summary>Вид накопителя на чертеже.</summary>
public enum DriveKind { Nvme, Sata25, Hdd35 }

/// <summary>Накопитель для чертежа: форм-фактор и надпись на корпусе (модель).</summary>
public sealed record DriveSpec(DriveKind Kind, string Label);
