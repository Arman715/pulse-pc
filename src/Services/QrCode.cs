using System.Text;

namespace PulsePC.Services;

/// <summary>
/// QR-код без сторонних библиотек — ровно столько, сколько нужно для короткого адреса:
/// байтовый режим, уровень коррекции M, версии 1–6 (до 106 байт). У этих версий один узор выравнивания
/// и нет блока сведений о версии, а блоки коррекции равной длины.
/// </summary>
public static class QrCode
{
    // Версии 1–6, уровень M (ISO/IEC 18004, таблица 9): всего кодовых слов, слов коррекции в блоке, число блоков.
    private static readonly (int Total, int EcPerBlock, int Blocks)[] Spec =
    {
        (26, 10, 1), (44, 16, 1), (70, 26, 1), (100, 18, 2), (134, 24, 2), (172, 16, 4),
    };

    public const int MaxVersion = 6;

    /// <summary>Сколько байт текста помещается в код данной версии (4 бита режима и 8 бит длины — служебные).</summary>
    public static int Capacity(int version)
    {
        var (total, ec, blocks) = Spec[version - 1];
        return ((total - ec * blocks) * 8 - 12) / 8;
    }

    /// <summary>Матрица модулей [строка, столбец], true — тёмный. null, если текст длиннее 106 байт.</summary>
    public static bool[,] Encode(string text) => Encode(Encoding.UTF8.GetBytes(text ?? ""));

    /// <param name="forceMask">Номер маски 0–7 для проверки; −1 — выбрать лучшую по правилам стандарта.</param>
    public static bool[,] Encode(byte[] data, int forceMask = -1)
    {
        int version = 0;
        for (int v = 1; v <= MaxVersion; v++) if (data.Length <= Capacity(v)) { version = v; break; }
        if (version == 0) return null;

        byte[] codewords = Interleave(version, DataCodewords(version, data));
        int size = 17 + 4 * version;
        var m = new bool[size, size];
        var fixedModule = new bool[size, size];
        DrawFunctionPatterns(m, fixedModule, version);
        DrawCodewords(m, fixedModule, codewords);

        int mask = forceMask;
        if (mask < 0)
        {
            int best = int.MaxValue;
            for (int i = 0; i < 8; i++)
            {
                ApplyMask(m, fixedModule, i);
                DrawFormat(m, fixedModule, i);
                int penalty = Penalty(m);
                if (penalty < best) { best = penalty; mask = i; }
                ApplyMask(m, fixedModule, i);      // повторное наложение снимает маску
            }
        }
        ApplyMask(m, fixedModule, mask);
        DrawFormat(m, fixedModule, mask);
        return m;
    }

    // ───────────── Данные ─────────────

    /// <summary>Поток бит: режим 0100, длина, байты, завершитель, выравнивание до байта, заполнители EC/11.</summary>
    private static byte[] DataCodewords(int version, byte[] data)
    {
        var (total, ec, blocks) = Spec[version - 1];
        int capacityBits = (total - ec * blocks) * 8;
        var bits = new List<bool>(capacityBits);
        Append(bits, 0b0100, 4);
        Append(bits, data.Length, 8);
        foreach (byte b in data) Append(bits, b, 8);
        Append(bits, 0, Math.Min(4, capacityBits - bits.Count));
        Append(bits, 0, (8 - bits.Count % 8) % 8);
        for (int pad = 0xEC; bits.Count < capacityBits; pad ^= 0xEC ^ 0x11) Append(bits, pad, 8);

        var result = new byte[capacityBits / 8];
        for (int i = 0; i < bits.Count; i++)
            if (bits[i]) result[i >> 3] |= (byte)(0x80 >> (i & 7));
        return result;
    }

    private static void Append(List<bool> bits, int value, int count)
    {
        for (int i = count - 1; i >= 0; i--) bits.Add(((value >> i) & 1) != 0);
    }

    /// <summary>Делит данные на блоки, дописывает к каждому коды Рида — Соломона и перемежает побайтно.</summary>
    private static byte[] Interleave(int version, byte[] data)
    {
        var (total, ecLen, blocks) = Spec[version - 1];
        int blockLen = data.Length / blocks;
        byte[] divisor = RsDivisor(ecLen);
        var ec = new byte[blocks][];
        for (int b = 0; b < blocks; b++) ec[b] = RsRemainder(data.AsSpan(b * blockLen, blockLen), divisor);

        var result = new byte[total];
        int k = 0;
        for (int i = 0; i < blockLen; i++)
            for (int b = 0; b < blocks; b++) result[k++] = data[b * blockLen + i];
        for (int i = 0; i < ecLen; i++)
            for (int b = 0; b < blocks; b++) result[k++] = ec[b][i];
        return result;
    }

    // ───────────── Коды Рида — Соломона над GF(2^8), многочлен x^8+x^4+x^3+x^2+1 ─────────────

    /// <summary>Коды коррекции для блока данных (открыт для самопроверки на примерах из стандарта).</summary>
    public static byte[] ErrorCorrection(byte[] data, int count) => RsRemainder(data, RsDivisor(count));

    private static byte[] RsDivisor(int degree)
    {
        var result = new byte[degree];
        result[degree - 1] = 1;
        int root = 1;
        for (int i = 0; i < degree; i++)
        {
            for (int j = 0; j < degree; j++)
            {
                result[j] = (byte)Mul(result[j], root);
                if (j + 1 < degree) result[j] ^= result[j + 1];
            }
            root = Mul(root, 2);
        }
        return result;
    }

    private static byte[] RsRemainder(ReadOnlySpan<byte> data, byte[] divisor)
    {
        var result = new byte[divisor.Length];
        foreach (byte b in data)
        {
            int factor = b ^ result[0];
            Array.Copy(result, 1, result, 0, result.Length - 1);
            result[^1] = 0;
            for (int i = 0; i < result.Length; i++) result[i] ^= (byte)Mul(divisor[i], factor);
        }
        return result;
    }

    private static int Mul(int x, int y)
    {
        int z = 0;
        for (int i = 7; i >= 0; i--)
        {
            z = (z << 1) ^ ((z >> 7) * 0x11D);
            z ^= ((y >> i) & 1) * x;
        }
        return z & 0xFF;
    }

    // ───────────── Рисунок ─────────────

    private static void DrawFunctionPatterns(bool[,] m, bool[,] fixedModule, int version)
    {
        int size = m.GetLength(0);
        void Set(int x, int y, bool dark) { m[y, x] = dark; fixedModule[y, x] = true; }

        // Линии синхронизации: шестая строка и шестой столбец, через один.
        for (int i = 0; i < size; i++) { Set(6, i, i % 2 == 0); Set(i, 6, i % 2 == 0); }

        // Три поисковых узора 7×7 с белой каймой.
        foreach (var (cx, cy) in new[] { (3, 3), (size - 4, 3), (3, size - 4) })
            for (int dy = -4; dy <= 4; dy++)
                for (int dx = -4; dx <= 4; dx++)
                {
                    int x = cx + dx, y = cy + dy;
                    if (x < 0 || x >= size || y < 0 || y >= size) continue;
                    int dist = Math.Max(Math.Abs(dx), Math.Abs(dy));
                    Set(x, y, dist != 2 && dist != 4);
                }

        // Узор выравнивания 5×5: у версий 2–6 он один, в правом нижнем углу.
        if (version >= 2)
        {
            int c = size - 7;
            for (int dy = -2; dy <= 2; dy++)
                for (int dx = -2; dx <= 2; dx++)
                    Set(c + dx, c + dy, Math.Max(Math.Abs(dx), Math.Abs(dy)) != 1);
        }

        // Место под сведения о формате (значение впишется после выбора маски).
        DrawFormat(m, fixedModule, 0);
    }

    /// <summary>15 бит формата: уровень M (00) и номер маски, код БЧХ (15,5), маска 101010000010010. Две копии.</summary>
    private static void DrawFormat(bool[,] m, bool[,] fixedModule, int mask)
    {
        int size = m.GetLength(0);
        int data = (0b00 << 3) | mask;
        int rem = data;
        for (int i = 0; i < 10; i++) rem = (rem << 1) ^ ((rem >> 9) * 0x537);
        int bits = ((data << 10) | rem) ^ 0x5412;
        bool Bit(int i) => ((bits >> i) & 1) != 0;
        void Set(int x, int y, bool dark) { m[y, x] = dark; fixedModule[y, x] = true; }

        // Первая копия — вокруг левого верхнего поискового узора.
        for (int i = 0; i <= 5; i++) Set(8, i, Bit(i));
        Set(8, 7, Bit(6));
        Set(8, 8, Bit(7));
        Set(7, 8, Bit(8));
        for (int i = 9; i < 15; i++) Set(14 - i, 8, Bit(i));

        // Вторая копия — под правым верхним и справа от левого нижнего.
        for (int i = 0; i < 8; i++) Set(size - 1 - i, 8, Bit(i));
        for (int i = 8; i < 15; i++) Set(8, size - 15 + i, Bit(i));
        Set(8, size - 8, true);        // всегда тёмный модуль
    }

    /// <summary>Биты данных змейкой парами столбцов справа налево, снизу вверх и обратно, мимо служебных модулей.</summary>
    private static void DrawCodewords(bool[,] m, bool[,] fixedModule, byte[] data)
    {
        int size = m.GetLength(0);
        int i = 0;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;                      // столбец синхронизации пропускается
            for (int vert = 0; vert < size; vert++)
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    bool upward = ((right + 1) & 2) == 0;
                    int y = upward ? size - 1 - vert : vert;
                    if (fixedModule[y, x] || i >= data.Length * 8) continue;
                    m[y, x] = ((data[i >> 3] >> (7 - (i & 7))) & 1) != 0;
                    i++;
                }
        }
    }

    private static void ApplyMask(bool[,] m, bool[,] fixedModule, int mask)
    {
        int size = m.GetLength(0);
        for (int y = 0; y < size; y++)
            for (int x = 0; x < size; x++)
            {
                if (fixedModule[y, x]) continue;
                bool invert = mask switch
                {
                    0 => (x + y) % 2 == 0,
                    1 => y % 2 == 0,
                    2 => x % 3 == 0,
                    3 => (x + y) % 3 == 0,
                    4 => (x / 3 + y / 2) % 2 == 0,
                    5 => x * y % 2 + x * y % 3 == 0,
                    6 => (x * y % 2 + x * y % 3) % 2 == 0,
                    _ => ((x + y) % 2 + x * y % 3) % 2 == 0,
                };
                if (invert) m[y, x] = !m[y, x];
            }
    }

    /// <summary>Штраф по четырём правилам стандарта: чем меньше, тем легче коду читаться.</summary>
    private static int Penalty(bool[,] m)
    {
        int size = m.GetLength(0), result = 0, dark = 0;
        for (int pass = 0; pass < 2; pass++)
        {
            for (int a = 0; a < size; a++)
            {
                // Правило 1: пять и более одноцветных модулей подряд.
                int run = 1;
                for (int b = 1; b < size; b++)
                {
                    bool same = pass == 0 ? m[a, b] == m[a, b - 1] : m[b, a] == m[b - 1, a];
                    if (same) { run++; if (run == 5) result += 3; else if (run > 5) result++; }
                    else run = 1;
                }
                // Правило 3: узор 1011101 с четырьмя светлыми модулями с одной из сторон (похож на поисковый).
                for (int b = 0; b + 11 <= size; b++)
                {
                    bool At(int k) => pass == 0 ? m[a, b + k] : m[b + k, a];
                    bool head = At(0) && !At(1) && At(2) && At(3) && At(4) && !At(5) && At(6) && !At(7) && !At(8) && !At(9) && !At(10);
                    bool tail = !At(0) && !At(1) && !At(2) && !At(3) && At(4) && !At(5) && At(6) && At(7) && At(8) && !At(9) && At(10);
                    if (head) result += 40;
                    if (tail) result += 40;
                }
            }
        }
        // Правило 2: одноцветные квадраты 2×2.
        for (int y = 0; y < size - 1; y++)
            for (int x = 0; x < size - 1; x++)
                if (m[y, x] == m[y, x + 1] && m[y, x] == m[y + 1, x] && m[y, x] == m[y + 1, x + 1]) result += 3;
        // Правило 4: доля тёмных модулей далеко от половины.
        foreach (bool b in m) if (b) dark++;
        int total = size * size;
        int k = (Math.Abs(dark * 20 - total * 10) + total - 1) / total - 1;
        return result + Math.Max(0, k) * 10;
    }

    // ───────────── Самопроверка ─────────────

    /// <summary>
    /// Разбирает готовую матрицу обратно независимым путём: читает формат, снимает маску, собирает кодовые слова,
    /// проверяет синдромы Рида — Соломона и достаёт байты. Возвращает текст или null, если код не сходится.
    /// </summary>
    public static string DecodeForTest(bool[,] m)
    {
        int size = m.GetLength(0);
        int version = (size - 17) / 4;
        if (version < 1 || version > MaxVersion || size != 17 + 4 * version) return null;

        // Формат читается из второй копии, чтобы проверить и её расположение.
        int bits = 0;
        for (int i = 0; i < 8; i++) if (m[8, size - 1 - i]) bits |= 1 << i;
        for (int i = 8; i < 15; i++) if (m[size - 15 + i, 8]) bits |= 1 << i;
        bits ^= 0x5412;
        int syndrome = bits;
        for (int i = 14; i >= 10; i--) if (((syndrome >> i) & 1) != 0) syndrome ^= 0x537 << (i - 10);
        if (syndrome != 0 || (bits >> 13) != 0) return null;
        int mask = (bits >> 10) & 7;

        var copy = (bool[,])m.Clone();
        var fixedModule = new bool[size, size];
        DrawFunctionPatterns(new bool[size, size], fixedModule, version);
        ApplyMask(copy, fixedModule, mask);

        var (total, ecLen, blocks) = Spec[version - 1];
        var raw = new byte[total];
        int n = 0;
        for (int right = size - 1; right >= 1; right -= 2)
        {
            if (right == 6) right = 5;
            for (int vert = 0; vert < size; vert++)
                for (int j = 0; j < 2; j++)
                {
                    int x = right - j;
                    int y = ((right + 1) & 2) == 0 ? size - 1 - vert : vert;
                    if (fixedModule[y, x] || n >= total * 8) continue;
                    if (copy[y, x]) raw[n >> 3] |= (byte)(0x80 >> (n & 7));
                    n++;
                }
        }

        int blockLen = (total - ecLen * blocks) / blocks;
        var data = new byte[blockLen * blocks];
        for (int b = 0; b < blocks; b++)
        {
            var block = new byte[blockLen + ecLen];
            for (int i = 0; i < blockLen; i++) block[i] = raw[i * blocks + b];
            for (int i = 0; i < ecLen; i++) block[blockLen + i] = raw[blockLen * blocks + i * blocks + b];
            // Значение многочлена блока в корнях α^0…α^(n−1) должно быть нулём.
            int root = 1;
            for (int s = 0; s < ecLen; s++)
            {
                int value = 0;
                foreach (byte c in block) value = Mul(value, root) ^ c;
                if (value != 0) return null;
                root = Mul(root, 2);
            }
            Array.Copy(block, 0, data, b * blockLen, blockLen);
        }

        if ((data[0] >> 4) != 0b0100) return null;
        int length = ((data[0] & 0x0F) << 4) | (data[1] >> 4);
        if (length > Capacity(version)) return null;
        var text = new byte[length];
        for (int i = 0; i < length; i++) text[i] = (byte)(((data[i + 1] & 0x0F) << 4) | (data[i + 2] >> 4));
        return Encoding.UTF8.GetString(text);
    }
}
