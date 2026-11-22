using System.Text;

namespace PulsePC.Services;

/// <summary>Модуль ядра из списка драйверов дампа.</summary>
public sealed class DumpModule
{
    public string Name = "";
    public ulong Base;
    public uint Size;
    public bool Contains(ulong address) => address >= Base && address < Base + Size;
}

/// <summary>Что удалось прочитать из малого дампа памяти (C:\Windows\Minidump\*.dmp).</summary>
public sealed class MinidumpInfo
{
    public string File = "";
    public DateTime Time;               // когда записан (из заголовка, иначе — время файла)
    public uint Code;                   // код остановки (BugCheckCode)
    public ulong[] Params = new ulong[4];
    public List<DumpModule> Modules = new();
    /// <summary>Драйвер-виновник: модуль, в который попадает адрес из параметров, адрес сбоя или стек. Пусто — не определён.</summary>
    public string Culprit = "";
    /// <summary>Виновник найден только по стеку (не по адресу из параметров) — тогда «вероятно».</summary>
    public bool CulpritFromStack;
    public string Error;                // почему не разобран (повреждён, не тот формат)
}

/// <summary>
/// Разбор малых дампов памяти Windows (формат «PAGEDU64», вид «triage»): заголовок с кодом остановки и параметрами,
/// список загруженных драйверов с адресами, стек сбоя. Виновный драйвер — тот, в который попадает адрес из параметров
/// (а если там только ядро Windows — первый «чужой» драйвер в стеке). Файлы только читаются; папка Minidump доступна
/// только администраторам.
/// </summary>
public static class Minidump
{
    // Смещения в заголовке DUMP_HEADER64 (0x2000 байт).
    private const int OffBugCheck = 0x38, OffParams = 0x40, OffContext = 0x348, OffRip = 0x348 + 0xF8, OffException = 0xF00,
                      OffDumpType = 0xF98, OffSystemTime = 0xFA8, HeaderSize = 0x2000;
    // TRIAGE_DUMP64 сразу за заголовком.
    private const int TContext = 0x0C, TException = 0x10, TCallStack = 0x28, TCallStackSize = 0x2C, TDriverList = 0x30, TDriverCount = 0x34;
    private const int TriageDump = 4;
    private const long MaxRead = 64L << 20;

    public static string Folder => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.Windows), "Minidump");

    /// <summary>Модули, которые почти всегда есть в стеке и сами виноваты редко: ядро и его ближайшие помощники.</summary>
    private static readonly HashSet<string> Core = new(StringComparer.OrdinalIgnoreCase)
    { "ntoskrnl.exe", "ntkrnlmp.exe", "ntkrnlpa.exe", "ntkrpamp.exe", "hal.dll", "halmacpi.dll", "kdcom.dll", "kd.dll", "win32k.sys", "win32kbase.sys", "win32kfull.sys" };

    /// <summary>Номер параметра (0–3), в котором для этого кода лежит адрес сбоя; −1 — нет такого.</summary>
    public static int AddressParam(uint code) => code switch
    {
        0x0A or 0xD1 or 0xD5 or 0xD6 => 3,          // адрес инструкции — четвёртый параметр
        0x1E or 0x3B or 0x7E or 0x8E or 0x1000007E or 0x1000008E => 1,
        0x50 or 0xBE or 0xCC or 0xCD => 2,
        0xFC => 0,
        0x116 or 0x117 or 0x119 or 0x10E => 1,      // видеодрайвер: указатель внутрь ответственного модуля
        _ => -1,
    };

    /// <summary>Разобрать файл дампа (читает не больше 64 МБ).</summary>
    public static MinidumpInfo Read(string path)
    {
        var info = new MinidumpInfo { File = path };
        try
        {
            info.Time = File.GetLastWriteTime(path);
            byte[] data;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            {
                int len = (int)Math.Min(fs.Length, MaxRead);
                data = new byte[len];
                int got = 0;
                while (got < len) { int n = fs.Read(data, got, len - got); if (n <= 0) break; got += n; }
                if (got < len) Array.Resize(ref data, got);
            }
            Parse(data, info);
        }
        catch (UnauthorizedAccessException) { info.Error = L.T("нужны права администратора"); }
        catch (Exception ex) { info.Error = ex.Message; }
        return info;
    }

    /// <summary>Разбор содержимого файла. Отдельно от чтения — для самопроверки на подставном дампе.</summary>
    public static void Parse(byte[] d, MinidumpInfo info)
    {
        if (d.Length < HeaderSize + 0x60 || Encoding.ASCII.GetString(d, 0, 4) != "PAGE" || Encoding.ASCII.GetString(d, 4, 4) != "DU64")
        {
            info.Error = L.T("не похоже на малый дамп памяти Windows (64 бит)");
            return;
        }
        info.Code = U32(d, OffBugCheck);
        for (int i = 0; i < 4; i++) info.Params[i] = U64(d, OffParams + i * 8);
        long ft = (long)U64(d, OffSystemTime);
        if (ft > 0) { try { info.Time = DateTime.FromFileTimeUtc(ft).ToLocalTime(); } catch { } }
        int dumpType = (int)U32(d, OffDumpType);
        if (dumpType != TriageDump)
        {
            info.Error = L.T("это полный дамп, список драйверов в нём программа не читает");
            return;
        }

        int t = HeaderSize;
        uint listOff = U32(d, t + TDriverList), count = U32(d, t + TDriverCount);
        info.Modules = Drivers(d, listOff, count);

        // Адреса-кандидаты: сначала параметр с адресом для этого кода, потом остальные параметры, потом адрес сбоя из контекста.
        var candidates = new List<ulong>();
        int ap = AddressParam(info.Code);
        if (ap >= 0) candidates.Add(info.Params[ap]);
        candidates.AddRange(info.Params);
        candidates.Add(U64(d, OffRip));
        candidates.Add(U64(d, OffException + 0x10));
        uint ctx = U32(d, t + TContext);
        if (ctx > 0 && ctx + 0x100 <= d.Length) candidates.Add(U64(d, (int)ctx + 0xF8));
        uint exc = U32(d, t + TException);
        if (exc > 0 && exc + 0x18 <= d.Length) candidates.Add(U64(d, (int)exc + 0x10));

        string coreHit = null;
        foreach (var a in candidates)
        {
            if (a < 0xFFFF800000000000UL) continue;
            var m = info.Modules.FirstOrDefault(x => x.Contains(a));
            if (m == null) continue;
            if (!Core.Contains(m.Name)) { info.Culprit = m.Name; return; }
            coreHit ??= m.Name;
        }

        // По адресам — только ядро Windows: смотрим стек, первый «чужой» драйвер на нём — вероятный виновник.
        uint stackOff = U32(d, t + TCallStack), stackSize = U32(d, t + TCallStackSize);
        if (stackOff > 0 && stackSize > 0 && stackOff < d.Length)
        {
            long end = Math.Min((long)stackOff + stackSize, d.Length) - 8;
            for (long p = stackOff; p <= end; p += 8)
            {
                ulong a = U64(d, (int)p);
                if (a < 0xFFFF800000000000UL) continue;
                var m = info.Modules.FirstOrDefault(x => x.Contains(a));
                if (m != null && !Core.Contains(m.Name)) { info.Culprit = m.Name; info.CulpritFromStack = true; return; }
            }
        }
        if (coreHit != null) info.Culprit = coreHit;
    }

    /// <summary>
    /// Список драйверов (DUMP_DRIVER_ENTRY64: смещение имени + KLDR_DATA_TABLE_ENTRY64). Размер записи по документации — 0x90,
    /// но на всякий случай подбирается: подходит тот вариант, у которого имена читаются, а адреса лежат в пространстве ядра.
    /// </summary>
    private static List<DumpModule> Drivers(byte[] d, uint listOff, uint count)
    {
        var none = new List<DumpModule>();
        if (count == 0 || count > 2000) return none;
        foreach (long baseOff in new long[] { 0, HeaderSize })
            foreach (int stride in new[] { 0x90, 0x98, 0x88, 0xA0 })
                foreach (int ldr in new[] { 8, 4 })
                {
                    long start = baseOff + listOff;
                    if (start <= 0 || start + (long)stride * count > d.Length) continue;
                    var list = new List<DumpModule>();
                    bool ok = true;
                    for (int i = 0; i < count && ok; i++)
                    {
                        int e = (int)(start + (long)i * stride);
                        uint nameOff = U32(d, e);
                        ulong dllBase = U64(d, e + ldr + 0x30);
                        uint size = U32(d, e + ldr + 0x40);
                        string name = DumpString(d, baseOff + nameOff);
                        if (name == null || dllBase < 0xFFFF800000000000UL || size == 0 || size > 0x40000000) { ok = i >= 3 && i >= count / 2; break; }
                        list.Add(new DumpModule { Name = name, Base = dllBase, Size = size });
                    }
                    if (ok && list.Count > 0) return list;
                }
        return none;
    }

    /// <summary>DUMP_STRING: ULONG длина в символах, затем UTF-16. null — не похоже на имя файла.</summary>
    private static string DumpString(byte[] d, long off)
    {
        if (off <= 0 || off + 4 > d.Length) return null;
        uint len = U32(d, (int)off);
        if (len == 0 || len > 260 || off + 4 + len * 2 > d.Length) return null;
        string s = Encoding.Unicode.GetString(d, (int)off + 4, (int)len * 2).TrimEnd('\0');
        if (s.Length == 0 || s.Any(c => c < 0x20) || !s.Contains('.')) return null;
        return Path.GetFileName(s);
    }

    private static uint U32(byte[] d, int off) => off >= 0 && off + 4 <= d.Length ? BitConverter.ToUInt32(d, off) : 0;
    private static ulong U64(byte[] d, int off) => off >= 0 && off + 8 <= d.Length ? BitConverter.ToUInt64(d, off) : 0;

    /// <summary>
    /// Малые дампы за последние days дней, новые первыми (не больше max). admin = false — папку прочитать нельзя.
    /// </summary>
    public static List<MinidumpInfo> Recent(int days, int max, out bool denied)
    {
        denied = false;
        var list = new List<MinidumpInfo>();
        try
        {
            if (!Directory.Exists(Folder)) return list;
            var files = new DirectoryInfo(Folder).GetFiles("*.dmp")
                .Where(f => f.LastWriteTime > DateTime.Now.AddDays(-days))
                .OrderByDescending(f => f.LastWriteTime).Take(max).ToList();
            foreach (var f in files)
            {
                var info = Read(f.FullName);
                if (info.Error == L.T("нужны права администратора")) denied = true;
                list.Add(info);
            }
        }
        catch (UnauthorizedAccessException) { denied = true; }
        catch (Exception ex) { Log.Error("малые дампы памяти", ex); }
        return list;
    }

    // ================================================================== подставной дамп для самопроверки
    /// <summary>
    /// Собрать подставной малый дамп: заголовок, раздел triage, список драйверов (имена в строковом пуле), стек.
    /// stack — адреса, которые лягут в стек.
    /// </summary>
    public static byte[] BuildFake(uint code, ulong[] p, (string Name, ulong Base, uint Size)[] modules, ulong[] stack = null, DateTime? at = null)
    {
        int listOff = HeaderSize + 0x100, entry = 0x90;
        int poolOff = listOff + entry * modules.Length;
        var pool = new List<byte>();
        var nameOffs = new List<int>();
        foreach (var m in modules)
        {
            nameOffs.Add(poolOff + pool.Count);
            string full = @"\SystemRoot\System32\drivers\" + m.Name;
            pool.AddRange(BitConverter.GetBytes((uint)full.Length));
            pool.AddRange(Encoding.Unicode.GetBytes(full + "\0"));
            while (pool.Count % 8 != 0) pool.Add(0);
        }
        int stackOff = poolOff + pool.Count;
        stack ??= Array.Empty<ulong>();
        var d = new byte[stackOff + stack.Length * 8 + 64];
        Encoding.ASCII.GetBytes("PAGE").CopyTo(d, 0);
        Encoding.ASCII.GetBytes("DU64").CopyTo(d, 4);
        BitConverter.GetBytes(code).CopyTo(d, OffBugCheck);
        for (int i = 0; i < 4; i++) BitConverter.GetBytes(p.Length > i ? p[i] : 0UL).CopyTo(d, OffParams + i * 8);
        BitConverter.GetBytes(TriageDump).CopyTo(d, OffDumpType);
        BitConverter.GetBytes((at ?? new DateTime(2026, 11, 20, 21, 14, 0)).ToFileTimeUtc()).CopyTo(d, OffSystemTime);
        BitConverter.GetBytes((uint)listOff).CopyTo(d, HeaderSize + TDriverList);
        BitConverter.GetBytes((uint)modules.Length).CopyTo(d, HeaderSize + TDriverCount);
        BitConverter.GetBytes((uint)stackOff).CopyTo(d, HeaderSize + TCallStack);
        BitConverter.GetBytes((uint)(stack.Length * 8)).CopyTo(d, HeaderSize + TCallStackSize);
        for (int i = 0; i < modules.Length; i++)
        {
            int e = listOff + i * entry;
            BitConverter.GetBytes((uint)nameOffs[i]).CopyTo(d, e);
            BitConverter.GetBytes(modules[i].Base).CopyTo(d, e + 8 + 0x30);
            BitConverter.GetBytes(modules[i].Size).CopyTo(d, e + 8 + 0x40);
        }
        pool.ToArray().CopyTo(d, poolOff);
        for (int i = 0; i < stack.Length; i++) BitConverter.GetBytes(stack[i]).CopyTo(d, stackOff + i * 8);
        return d;
    }
}
