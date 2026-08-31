// SimtOptimizer - optimalizace obsahu hry Simt Simulator.
//
// Delá tri veci:
//   1. vypne debug mód hry (soubor .ini vedle .exe, který zapne optimalizace JIT)
//   2. prevede nekomprimované textury do blokové komprese DXT
//   3. dogeneruje mipmapy tam, kde chybí a kde je to bezpecné
//
// Kompiluje se za behu pres Add-Type, takze musí zustat v ramci C# 5.
// Zádná interpolace retezcu, zádné expression-bodied cleny.
using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace SimtOptimizer
{
    // ------------------------------------------------------------------ log

    internal static class Log
    {
        private static string _path;
        private static readonly object Sync = new object();

        public static void Start(string dir)
        {
            _path = Path.Combine(dir, "SimtOptimizer.log");
            Write("=== SimtOptimizer spusten " + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " ===");
        }

        public static void Write(string msg)
        {
            if (_path == null) return;
            lock (Sync)
            {
                try { File.AppendAllText(_path, DateTime.Now.ToString("HH:mm:ss") + "  " + msg + Environment.NewLine); }
                catch { }
            }
        }

        public static string Path_ { get { return _path; } }
    }

    // ------------------------------------------------------- XNB / textury

    internal class XnbTexture
    {
        public int Format;
        public int Width;
        public int Height;
        public string ReaderName;
        public int ReaderVersion;
        public List<byte[]> Data = new List<byte[]>();

        public int Levels { get { return Data.Count; } }
    }

    internal static class Xnb
    {
        public const int FmtColor = 0;
        public const int FmtDxt1 = 4;
        public const int FmtDxt3 = 5;
        public const int FmtDxt5 = 6;

        // -------------------------------------------------------- ctení

        public static XnbTexture Read(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            using (BinaryReader br = new BinaryReader(fs))
            {
                if (br.ReadByte() != 'X' || br.ReadByte() != 'N' || br.ReadByte() != 'B')
                    throw new InvalidDataException("neni XNB");
                br.ReadByte();                        // platforma
                br.ReadByte();                        // verze
                int flags = br.ReadByte();
                br.ReadUInt32();                      // velikost souboru
                if ((flags & 0x80) != 0)
                    throw new NotSupportedException("LZX komprimovany");

                int count = Read7(br);
                string first = null;
                int firstVer = 0;
                for (int i = 0; i < count; i++)
                {
                    string name = br.ReadString();
                    int ver = br.ReadInt32();
                    if (i == 0) { first = name; firstVer = ver; }
                }
                if (count != 1 || first == null || !IsTexture2DReader(first))
                    throw new NotSupportedException("neni Texture2D");
                if (Read7(br) != 0)
                    throw new NotSupportedException("sdilene prostredky");
                Read7(br);                            // index type readeru

                XnbTexture t = new XnbTexture();
                t.ReaderName = first;
                t.ReaderVersion = firstVer;
                t.Format = br.ReadInt32();
                t.Width = br.ReadInt32();
                t.Height = br.ReadInt32();
                int levels = br.ReadInt32();
                for (int i = 0; i < levels; i++)
                {
                    int n = br.ReadInt32();
                    t.Data.Add(br.ReadBytes(n));
                }
                return t;
            }
        }

        // Obsah vyrobeny ruznymi verzemi XNA/MonoGame pojmenovava reader bud
        // zkracene, nebo plne kvalifikovane. Pri zapisu se jmeno zachova.
        private static bool IsTexture2DReader(string name)
        {
            int comma = name.IndexOf(',');
            string type = comma > 0 ? name.Substring(0, comma) : name;
            return type.Trim().EndsWith(".Texture2DReader", StringComparison.Ordinal);
        }

        private static int Read7(BinaryReader br)
        {
            int v = 0, shift = 0, b;
            do { b = br.ReadByte(); v |= (b & 0x7f) << shift; shift += 7; } while ((b & 0x80) != 0);
            return v;
        }

        private static void Write7(BinaryWriter bw, int v)
        {
            while (v >= 0x80) { bw.Write((byte)(v | 0x80)); v >>= 7; }
            bw.Write((byte)v);
        }

        // --------------------------------------------------------- zápis

        public static void Write(string path, XnbTexture t)
        {
            byte[] buf;
            using (MemoryStream ms = new MemoryStream())
            {
                using (BinaryWriter bw = new BinaryWriter(ms, Encoding.UTF8, true))
                {
                    bw.Write((byte)'X'); bw.Write((byte)'N'); bw.Write((byte)'B');
                    bw.Write((byte)'w'); bw.Write((byte)5); bw.Write((byte)0);
                    bw.Write((uint)0);                       // doplní se níze
                    Write7(bw, 1);
                    bw.Write(t.ReaderName);
                    bw.Write(t.ReaderVersion);
                    Write7(bw, 0);                           // sdílené prostredky
                    Write7(bw, 1);                           // index type readeru
                    bw.Write(t.Format);
                    bw.Write(t.Width);
                    bw.Write(t.Height);
                    bw.Write(t.Data.Count);
                    for (int i = 0; i < t.Data.Count; i++)
                    {
                        bw.Write(t.Data[i].Length);
                        bw.Write(t.Data[i]);
                    }
                }
                buf = ms.ToArray();
            }
            Array.Copy(BitConverter.GetBytes((uint)buf.Length), 0, buf, 6, 4);
            File.WriteAllBytes(path, buf);
        }

        // ------------------------------------------------- dekóde do RGBA

        public static byte[] ToRgba(XnbTexture t, int level)
        {
            int w = Math.Max(1, t.Width >> level);
            int h = Math.Max(1, t.Height >> level);
            byte[] src = t.Data[level];
            byte[] dst = new byte[w * h * 4];
            switch (t.Format)
            {
                case FmtColor:
                    Array.Copy(src, dst, Math.Min(src.Length, dst.Length));
                    return dst;
                case FmtDxt1: DecodeBc(src, dst, w, h, false, false); return dst;
                case FmtDxt3: DecodeBc(src, dst, w, h, true, true); return dst;
                case FmtDxt5: DecodeBc(src, dst, w, h, true, false); return dst;
                default: throw new NotSupportedException("format " + t.Format);
            }
        }

        private static void DecodeBc(byte[] src, byte[] dst, int w, int h, bool alphaBlock, bool explicitAlpha)
        {
            int bw = Math.Max(1, (w + 3) / 4);
            int bh = Math.Max(1, (h + 3) / 4);
            int stride = alphaBlock ? 16 : 8;
            int p = 0;
            byte[] a = new byte[16];
            int[] r = new int[4], g = new int[4], b = new int[4], al = new int[4];

            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++, p += stride)
                {
                    int cOff = p;
                    for (int i = 0; i < 16; i++) a[i] = 255;

                    if (alphaBlock)
                    {
                        cOff = p + 8;
                        if (explicitAlpha)
                        {
                            for (int i = 0; i < 16; i++)
                            {
                                int nib = src[p + (i >> 1)];
                                int v = (i & 1) == 0 ? (nib & 0xF) : (nib >> 4);
                                a[i] = (byte)(v * 17);
                            }
                        }
                        else
                        {
                            int a0 = src[p], a1 = src[p + 1];
                            int[] pal = new int[8];
                            pal[0] = a0; pal[1] = a1;
                            if (a0 > a1)
                            {
                                for (int i = 1; i < 7; i++) pal[i + 1] = ((7 - i) * a0 + i * a1) / 7;
                            }
                            else
                            {
                                for (int i = 1; i < 5; i++) pal[i + 1] = ((5 - i) * a0 + i * a1) / 5;
                                pal[6] = 0; pal[7] = 255;
                            }
                            long bits = 0;
                            for (int i = 0; i < 6; i++) bits |= (long)src[p + 2 + i] << (8 * i);
                            for (int i = 0; i < 16; i++) a[i] = (byte)pal[(int)((bits >> (3 * i)) & 7)];
                        }
                    }

                    int c0 = src[cOff] | (src[cOff + 1] << 8);
                    int c1 = src[cOff + 2] | (src[cOff + 3] << 8);
                    Unpack565(c0, out r[0], out g[0], out b[0]); al[0] = 255;
                    Unpack565(c1, out r[1], out g[1], out b[1]); al[1] = 255;
                    if (c0 > c1 || alphaBlock)
                    {
                        r[2] = (2 * r[0] + r[1]) / 3; g[2] = (2 * g[0] + g[1]) / 3; b[2] = (2 * b[0] + b[1]) / 3; al[2] = 255;
                        r[3] = (r[0] + 2 * r[1]) / 3; g[3] = (g[0] + 2 * g[1]) / 3; b[3] = (b[0] + 2 * b[1]) / 3; al[3] = 255;
                    }
                    else
                    {
                        r[2] = (r[0] + r[1]) / 2; g[2] = (g[0] + g[1]) / 2; b[2] = (b[0] + b[1]) / 2; al[2] = 255;
                        r[3] = 0; g[3] = 0; b[3] = 0; al[3] = 0;
                    }

                    uint idx = BitConverter.ToUInt32(src, cOff + 4);
                    for (int i = 0; i < 16; i++)
                    {
                        int px = bx * 4 + (i & 3);
                        int py = by * 4 + (i >> 2);
                        if (px >= w || py >= h) continue;
                        int k = (py * w + px) * 4;
                        int s = (int)((idx >> (2 * i)) & 3);
                        dst[k] = (byte)r[s];
                        dst[k + 1] = (byte)g[s];
                        dst[k + 2] = (byte)b[s];
                        dst[k + 3] = (byte)(alphaBlock ? a[i] : al[s]);
                    }
                }
            }
        }

        private static void Unpack565(int v, out int r, out int g, out int b)
        {
            r = (v >> 11) & 31; r = (r << 3) | (r >> 2);
            g = (v >> 5) & 63; g = (g << 2) | (g >> 4);
            b = v & 31; b = (b << 3) | (b >> 2);
        }

        // ------------------------------------------------------ zmensení

        public static byte[] Downsample(byte[] src, int w, int h, out int nw, out int nh)
        {
            nw = Math.Max(1, w / 2);
            nh = Math.Max(1, h / 2);
            byte[] dst = new byte[nw * nh * 4];
            for (int y = 0; y < nh; y++)
            {
                for (int x = 0; x < nw; x++)
                {
                    int x0 = Math.Min(x * 2, w - 1), x1 = Math.Min(x * 2 + 1, w - 1);
                    int y0 = Math.Min(y * 2, h - 1), y1 = Math.Min(y * 2 + 1, h - 1);
                    int o = (y * nw + x) * 4;
                    for (int c = 0; c < 4; c++)
                    {
                        int s = src[(y0 * w + x0) * 4 + c] + src[(y0 * w + x1) * 4 + c]
                              + src[(y1 * w + x0) * 4 + c] + src[(y1 * w + x1) * 4 + c];
                        dst[o + c] = (byte)((s + 2) / 4);
                    }
                }
            }
            return dst;
        }

        public static int MipCount(int w, int h)
        {
            int n = 1;
            while (w > 1 || h > 1) { w = Math.Max(1, w / 2); h = Math.Max(1, h / 2); n++; }
            return n;
        }

        // ---------------------------------------------------- BC1 / BC3

        public static byte[] EncodeBc(byte[] rgba, int w, int h, bool dxt5)
        {
            int bw = Math.Max(1, (w + 3) / 4);
            int bh = Math.Max(1, (h + 3) / 4);
            int stride = dxt5 ? 16 : 8;
            byte[] outBuf = new byte[bw * bh * stride];
            byte[] blk = new byte[64];

            for (int by = 0; by < bh; by++)
            {
                for (int bx = 0; bx < bw; bx++)
                {
                    for (int i = 0; i < 16; i++)
                    {
                        int px = Math.Min(bx * 4 + (i & 3), w - 1);
                        int py = Math.Min(by * 4 + (i >> 2), h - 1);
                        int s = (py * w + px) * 4;
                        blk[i * 4] = rgba[s];
                        blk[i * 4 + 1] = rgba[s + 1];
                        blk[i * 4 + 2] = rgba[s + 2];
                        blk[i * 4 + 3] = rgba[s + 3];
                    }
                    int o = (by * bw + bx) * stride;
                    if (dxt5)
                    {
                        EncodeAlphaBc3(blk, outBuf, o);
                        EncodeColorBlock(blk, outBuf, o + 8, true);
                    }
                    else
                    {
                        EncodeColorBlock(blk, outBuf, o, false);
                    }
                }
            }
            return outBuf;
        }

        private static void EncodeAlphaBc3(byte[] blk, byte[] dst, int o)
        {
            int mn = 255, mx = 0;
            for (int i = 0; i < 16; i++)
            {
                int a = blk[i * 4 + 3];
                if (a < mn) mn = a;
                if (a > mx) mx = a;
            }
            if (mx == mn)
            {
                dst[o] = (byte)mx; dst[o + 1] = (byte)mx;
                for (int i = 0; i < 6; i++) dst[o + 2 + i] = 0;
                return;
            }
            byte a0 = (byte)mx, a1 = (byte)mn;
            int[] pal = new int[8];
            pal[0] = a0; pal[1] = a1;
            for (int i = 1; i < 7; i++) pal[i + 1] = ((7 - i) * a0 + i * a1) / 7;

            long bits = 0;
            for (int i = 0; i < 16; i++)
            {
                int a = blk[i * 4 + 3], best = 0, bd = int.MaxValue;
                for (int k = 0; k < 8; k++)
                {
                    int d = Math.Abs(a - pal[k]);
                    if (d < bd) { bd = d; best = k; }
                }
                bits |= (long)best << (3 * i);
            }
            dst[o] = a0; dst[o + 1] = a1;
            for (int i = 0; i < 6; i++) dst[o + 2 + i] = (byte)((bits >> (8 * i)) & 0xFF);
        }

        private static void EncodeColorBlock(byte[] blk, byte[] dst, int o, bool forceFourColor)
        {
            // Obalový kvádr barev, mírne stazený dovnitr, aby jediný odlehlý
            // texel neroztáhl koncové body pres celý blok.
            int[] mn = new int[] { 255, 255, 255 };
            int[] mx = new int[] { 0, 0, 0 };
            int used = 0;
            for (int i = 0; i < 16; i++)
            {
                if (!forceFourColor && blk[i * 4 + 3] < 128) continue;
                used++;
                for (int c = 0; c < 3; c++)
                {
                    int v = blk[i * 4 + c];
                    if (v < mn[c]) mn[c] = v;
                    if (v > mx[c]) mx[c] = v;
                }
            }
            if (used == 0) { for (int c = 0; c < 3; c++) { mn[c] = 0; mx[c] = 0; } }
            for (int c = 0; c < 3; c++)
            {
                int inset = (mx[c] - mn[c]) >> 4;
                mn[c] = Math.Min(255, mn[c] + inset);
                mx[c] = Math.Max(0, mx[c] - inset);
                if (mn[c] > mx[c]) { int tmp = mn[c]; mn[c] = mx[c]; mx[c] = tmp; }
            }

            int c0 = Pack565(mx[0], mx[1], mx[2]);
            int c1 = Pack565(mn[0], mn[1], mn[2]);
            bool threeColor = !forceFourColor && used < 16;      // blok má prusvitné texely
            if (threeColor) { if (c0 > c1) { int t = c0; c0 = c1; c1 = t; } }
            else { if (c0 < c1) { int t = c0; c0 = c1; c1 = t; } }

            int[] pr = new int[4], pg = new int[4], pb = new int[4];
            BuildPalette(c0, c1, !threeColor, pr, pg, pb);

            uint idx = 0;
            int lim = threeColor ? 3 : 4;
            for (int i = 0; i < 16; i++)
            {
                if (threeColor && blk[i * 4 + 3] < 128) { idx |= 3u << (2 * i); continue; }
                int r = blk[i * 4], g = blk[i * 4 + 1], b = blk[i * 4 + 2];
                int best = 0, bd = int.MaxValue;
                for (int k = 0; k < lim; k++)
                {
                    int dr = r - pr[k], dg = g - pg[k], db = b - pb[k];
                    int d = 3 * dr * dr + 6 * dg * dg + db * db;    // vnímané váhy
                    if (d < bd) { bd = d; best = k; }
                }
                idx |= (uint)best << (2 * i);
            }

            dst[o] = (byte)(c0 & 0xFF);
            dst[o + 1] = (byte)(c0 >> 8);
            dst[o + 2] = (byte)(c1 & 0xFF);
            dst[o + 3] = (byte)(c1 >> 8);
            Array.Copy(BitConverter.GetBytes(idx), 0, dst, o + 4, 4);
        }

        private static void BuildPalette(int c0, int c1, bool four, int[] pr, int[] pg, int[] pb)
        {
            Unpack565(c0, out pr[0], out pg[0], out pb[0]);
            Unpack565(c1, out pr[1], out pg[1], out pb[1]);
            if (four)
            {
                pr[2] = (2 * pr[0] + pr[1]) / 3; pg[2] = (2 * pg[0] + pg[1]) / 3; pb[2] = (2 * pb[0] + pb[1]) / 3;
                pr[3] = (pr[0] + 2 * pr[1]) / 3; pg[3] = (pg[0] + 2 * pg[1]) / 3; pb[3] = (pb[0] + 2 * pb[1]) / 3;
            }
            else
            {
                pr[2] = (pr[0] + pr[1]) / 2; pg[2] = (pg[0] + pg[1]) / 2; pb[2] = (pb[0] + pb[1]) / 2;
                pr[3] = 0; pg[3] = 0; pb[3] = 0;
            }
        }

        private static int Pack565(int r, int g, int b)
        {
            return ((r >> 3) << 11) | ((g >> 2) << 5) | (b >> 3);
        }

        public static bool HasAlpha(byte[] rgba, out bool binary)
        {
            bool any = false;
            binary = true;
            for (int i = 3; i < rgba.Length; i += 4)
            {
                byte a = rgba[i];
                if (a != 255) any = true;
                if (a != 0 && a != 255) binary = false;
            }
            return any;
        }
    }

    // -------------------------------------------------------------- jádro

    internal class GameInfo
    {
        public string GameDir;
        public string ContentDir;
        public string ExePath;
        public string Version;
        public string BackupDir;
    }

    internal class Plan
    {
        public List<string> Files = new List<string>();      // cesty relativní ke Content
        public long OriginalBytes;
    }

    internal delegate void Progress(int percent, string message);

    internal class RunResult
    {
        public int Total;
        public int Ok;
        public int Failed;
        public long OriginalBytes;
        public long NewBytes;
        public bool NothingToDo;

        public long SavedMb { get { return (OriginalBytes - NewBytes) / 1048576; } }
    }

    internal static class Core
    {
        public const string ExpectedVersion = "1.8.101.0";

        // Slozky, které se nikdy nesahají.
        //  Skybox   - obsahuje zdroje cubemap, které hra cte zpet na CPU
        //             pres GetData<Color>; blokovou kompresi by neprezila.
        //  Grafika, NoveMenu, Editor - UI grafika, na ostrých hranách je
        //             komprese videt a jde o zanedbatelné mnozství dat.
        private static readonly string[] SkipDirs = new string[]
        {
            @"Soubory\Modely\Skybox",
            @"Soubory\Grafika",
            @"Soubory\NoveMenu",
            @"Soubory\Editor"
        };

        // ----------------------------------------------------- detekce hry

        public static GameInfo Detect(string dir, out string problem)
        {
            problem = null;
            if (string.IsNullOrEmpty(dir) || !Directory.Exists(dir))
            {
                problem = "Slozka neexistuje.";
                return null;
            }

            string exe = Path.Combine(dir, "SimtSimulator.exe");
            string content = Path.Combine(dir, "Content");
            string mono = Path.Combine(dir, "MonoGame.Framework.dll");

            if (!File.Exists(exe)) { problem = "Ve slozce není SimtSimulator.exe."; return null; }
            if (!Directory.Exists(content)) { problem = "Ve slozce není podslozka Content."; return null; }
            if (!File.Exists(mono)) { problem = "Ve slozce není MonoGame.Framework.dll."; return null; }

            GameInfo g = new GameInfo();
            g.GameDir = dir;
            g.ContentDir = content;
            g.ExePath = exe;
            try { g.Version = FileVersionInfo.GetVersionInfo(exe).FileVersion; }
            catch { g.Version = "?"; }

            string parent = null;
            try { parent = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar)); }
            catch { }
            g.BackupDir = string.IsNullOrEmpty(parent)
                ? Path.Combine(dir, "_zaloha_original_textury")
                : Path.Combine(parent, "_zaloha_original_textury");

            return g;
        }

        public static bool IsGameRunning()
        {
            try { return Process.GetProcessesByName("SimtSimulator").Length > 0
                       || Process.GetProcessesByName("SimtSimulator32").Length > 0; }
            catch { return false; }
        }

        public static bool BackupExists(GameInfo g)
        {
            return Directory.Exists(g.BackupDir)
                && Directory.EnumerateFiles(g.BackupDir, "*.xnb", SearchOption.AllDirectories).Any();
        }

        // ------------------------------------------------- výber souboru

        // Vrací seznam textur, které je bezpecné prevést. Kritéria se odvozují
        // z obsahu souboru, ne z pevného seznamu, aby to fungovalo i na jiných
        // verzích hry.
        public static Plan BuildPlan(GameInfo g, Action<int> onProgress)
        {
            Plan plan = new Plan();
            string[] all = Directory.GetFiles(g.ContentDir, "*.xnb", SearchOption.AllDirectories);
            int done = 0;

            foreach (string full in all)
            {
                done++;
                if (onProgress != null && (done % 500) == 0)
                    onProgress((int)(100L * done / Math.Max(1, all.Length)));

                string rel = full.Substring(g.ContentDir.Length + 1);
                if (IsSkipped(rel)) continue;

                XnbTexture t;
                try { t = ReadHeaderOnly(full); }
                catch { continue; }                       // LZX, model, font, cokoli jiného
                if (t == null) continue;

                // Zdroj cubemapy v krízovém rozlození 4:3. Hru by to shodilo,
                // protoze si tyhle textury cte zpet jako pole pixelu.
                if (t.Width * 3 == t.Height * 4) continue;

                // Bloková komprese pracuje po ctvercích 4x4.
                if (t.Width % 4 != 0 || t.Height % 4 != 0 || t.Width < 4 || t.Height < 4) continue;

                bool worthIt;
                if (t.Format == Xnb.FmtColor)
                {
                    worthIt = true;                                // nekomprimovaná -> zkomprimovat
                }
                else if ((t.Format == Xnb.FmtDxt1 || t.Format == Xnb.FmtDxt3 || t.Format == Xnb.FmtDxt5)
                         && t.Levels <= 1)
                {
                    // Uz komprimovaná a bez mipmap. Dogenerovat je smíme jen u
                    // neprusvitné textury — u alfa-testované by se objekt na dálku
                    // ztratil. Prusvitnost pozná az dekódování, takze se sem musí
                    // sáhnout doopravdy. Zároven to drzí nástroj idempotentní:
                    // pri druhém spustení uz nic k práci nezbyde.
                    worthIt = false;
                    try
                    {
                        XnbTexture whole = Xnb.Read(full);
                        bool binary;
                        worthIt = !Xnb.HasAlpha(Xnb.ToRgba(whole, 0), out binary);
                    }
                    catch { }
                }
                else
                {
                    worthIt = false;
                }

                if (!worthIt) continue;

                plan.Files.Add(rel);
                try { plan.OriginalBytes += new FileInfo(full).Length; }
                catch { }
            }
            if (onProgress != null) onProgress(100);
            return plan;
        }

        private static bool IsSkipped(string rel)
        {
            for (int i = 0; i < SkipDirs.Length; i++)
                if (rel.StartsWith(SkipDirs[i], StringComparison.OrdinalIgnoreCase))
                    return true;
            return false;
        }

        // Precte jen hlavicku; data úrovní se preskocí, aby scan byl rychlý.
        private static XnbTexture ReadHeaderOnly(string path)
        {
            using (FileStream fs = File.OpenRead(path))
            using (BinaryReader br = new BinaryReader(fs))
            {
                if (fs.Length < 24) return null;
                if (br.ReadByte() != 'X' || br.ReadByte() != 'N' || br.ReadByte() != 'B') return null;
                br.ReadByte();
                br.ReadByte();
                int flags = br.ReadByte();
                br.ReadUInt32();
                if ((flags & 0x80) != 0) return null;

                int count = ReadHeader7(br);
                if (count != 1) return null;
                string name = br.ReadString();
                br.ReadInt32();
                int comma = name.IndexOf(',');
                string type = comma > 0 ? name.Substring(0, comma) : name;
                if (!type.Trim().EndsWith(".Texture2DReader", StringComparison.Ordinal)) return null;
                if (ReadHeader7(br) != 0) return null;
                ReadHeader7(br);

                XnbTexture t = new XnbTexture();
                t.Format = br.ReadInt32();
                t.Width = br.ReadInt32();
                t.Height = br.ReadInt32();
                int levels = br.ReadInt32();
                for (int i = 0; i < levels; i++) t.Data.Add(null);   // jen pocet
                return t;
            }
        }

        private static int ReadHeader7(BinaryReader br)
        {
            int v = 0, shift = 0, b;
            do { b = br.ReadByte(); v |= (b & 0x7f) << shift; shift += 7; } while ((b & 0x80) != 0);
            return v;
        }

        // ------------------------------------------------------- prevod

        // Politika mipmap. Zmena mip retezce mení, jak textura vypadá na dálku,
        // a u alfa-testované vegetace to znamená, ze strom z dálky zmizí. Proto:
        //   zdroj uz mipmapy má   -> prekomprimovat autorovy vlastní úrovne
        //   nemá je a je neprusvitný -> mipmapy dogenerovat
        //   nemá je a má alfu     -> nechat jednu úroven
        public static void ConvertOne(string srcPath, string dstPath)
        {
            XnbTexture t = Xnb.Read(srcPath);
            byte[] rgba0 = Xnb.ToRgba(t, 0);

            bool binary;
            bool alpha = Xnb.HasAlpha(rgba0, out binary);
            int fmt = (alpha && !binary) ? Xnb.FmtDxt5 : Xnb.FmtDxt1;

            bool alreadyBc = (t.Format == Xnb.FmtDxt1 || t.Format == Xnb.FmtDxt5);
            bool keepLevel0 = alreadyBc && fmt == t.Format;

            XnbTexture res = new XnbTexture();
            res.Format = fmt;
            res.Width = t.Width;
            res.Height = t.Height;
            res.ReaderName = t.ReaderName;
            res.ReaderVersion = t.ReaderVersion;

            if (t.Levels > 1)
            {
                for (int l = 0; l < t.Levels; l++)
                {
                    if (l == 0 && keepLevel0) { res.Data.Add(t.Data[0]); continue; }
                    int lw = Math.Max(1, t.Width >> l);
                    int lh = Math.Max(1, t.Height >> l);
                    res.Data.Add(Xnb.EncodeBc(Xnb.ToRgba(t, l), lw, lh, fmt == Xnb.FmtDxt5));
                }
            }
            else
            {
                int levels = alpha ? 1 : Xnb.MipCount(t.Width, t.Height);
                int cw = t.Width, ch = t.Height;
                byte[] cur = rgba0;
                for (int l = 0; l < levels; l++)
                {
                    if (l > 0)
                    {
                        int nw, nh;
                        cur = Xnb.Downsample(cur, cw, ch, out nw, out nh);
                        cw = nw; ch = nh;
                    }
                    if (l == 0 && keepLevel0) res.Data.Add(t.Data[0]);
                    else res.Data.Add(Xnb.EncodeBc(cur, cw, ch, fmt == Xnb.FmtDxt5));
                }
            }

            Xnb.Write(dstPath, res);
            VerifyWritten(dstPath, res);
        }

        // Zpetná kontrola: soubor se znovu precte a porovná s tím, co se melo
        // zapsat. Chytí chybu zápisu drív, nez ji najde hra.
        private static void VerifyWritten(string path, XnbTexture expected)
        {
            XnbTexture back = Xnb.Read(path);
            if (back.Format != expected.Format || back.Width != expected.Width
                || back.Height != expected.Height || back.Levels != expected.Levels)
                throw new InvalidDataException("kontrola zapsaného souboru selhala: " + path);
            for (int i = 0; i < back.Levels; i++)
                if (back.Data[i].Length != expected.Data[i].Length)
                    throw new InvalidDataException("kontrola úrovne " + i + " selhala: " + path);
        }

        // --------------------------------------------------- debug mód

        public static void WriteIni(GameInfo g)
        {
            string[] names = new string[] { "SimtSimulator", "SimtSimulator32" };
            string body = "[.NET Framework Debugging Control]\r\nGenerateTrackingInfo=0\r\nAllowOptimize=1\r\n";
            for (int i = 0; i < names.Length; i++)
            {
                string exe = Path.Combine(g.GameDir, names[i] + ".exe");
                if (!File.Exists(exe)) continue;
                File.WriteAllText(Path.Combine(g.GameDir, names[i] + ".ini"), body, Encoding.ASCII);
                Log.Write("zapsáno " + names[i] + ".ini");
            }
        }

        public static void RemoveIni(GameInfo g)
        {
            string[] names = new string[] { "SimtSimulator.ini", "SimtSimulator32.ini" };
            for (int i = 0; i < names.Length; i++)
            {
                string p = Path.Combine(g.GameDir, names[i]);
                try { if (File.Exists(p)) { File.Delete(p); Log.Write("smazáno " + names[i]); } }
                catch (Exception ex) { Log.Write("nelze smazat " + names[i] + ": " + ex.Message); }
            }
        }

        // ----------------------------------------------------- spustení

        public static long FreeSpace(string path)
        {
            try { return new DriveInfo(Path.GetPathRoot(Path.GetFullPath(path))).AvailableFreeSpace; }
            catch { return long.MaxValue; }
        }

        // Kazdý soubor se nejdrív zkopíruje do zálohy a teprve pak prepíse.
        // Kdyz cokoli selze v pulce, zálohu uz má a jde se vrátit zpet.
        public static RunResult Optimize(GameInfo g, Progress prog)
        {
            Log.Write("optimalizace: " + g.GameDir + " (verze " + g.Version + ")");
            prog(0, "Analyzuji obsah hry…");

            Plan plan = BuildPlan(g, delegate(int pct) { prog(pct / 10, "Analyzuji obsah hry…"); });
            Log.Write("k prevodu: " + plan.Files.Count + " souboru, "
                    + (plan.OriginalBytes / 1048576) + " MB");

            RunResult res = new RunResult();
            res.Total = plan.Files.Count;
            res.OriginalBytes = plan.OriginalBytes;

            if (plan.Files.Count == 0)
            {
                WriteIni(g);
                res.NothingToDo = true;
                prog(100, "Hotovo.");
                return res;
            }

            long needed = plan.OriginalBytes + (100L * 1024 * 1024);
            long free = FreeSpace(g.BackupDir);
            if (free < needed)
                throw new IOException("Na disku není dost místa. Potřeba je zhruba "
                    + (needed / 1048576) + " MB, volných je " + (free / 1048576) + " MB.");

            Directory.CreateDirectory(g.BackupDir);

            int total = plan.Files.Count;
            int done = 0;
            int failed = 0;
            long newBytes = 0;

            ParallelOptions po = new ParallelOptions();
            po.MaxDegreeOfParallelism = Math.Max(1, Environment.ProcessorCount);

            Parallel.ForEach(plan.Files, po, delegate(string rel)
            {
                string src = Path.Combine(g.ContentDir, rel);
                string bak = Path.Combine(g.BackupDir, rel);
                string tmp = src + ".tmp_opt";
                try
                {
                    if (!File.Exists(bak))
                    {
                        Directory.CreateDirectory(Path.GetDirectoryName(bak));
                        File.Copy(src, bak, false);
                    }
                    ConvertOne(src, tmp);
                    File.Copy(tmp, src, true);
                    File.Delete(tmp);
                    Interlocked.Add(ref newBytes, new FileInfo(src).Length);
                }
                catch (Exception ex)
                {
                    try { if (File.Exists(tmp)) File.Delete(tmp); }
                    catch { }
                    Interlocked.Increment(ref failed);
                    Log.Write("preskoceno " + rel + ": " + ex.Message);
                }

                int d = Interlocked.Increment(ref done);
                if ((d % 5) == 0 || d == total)
                    prog(10 + (int)(85L * d / total), "Převádím textury…   " + d + " / " + total);
            });

            prog(96, "Vypínám debug mód…");
            WriteIni(g);
            prog(100, "Hotovo.");

            res.Failed = failed;
            res.Ok = total - failed;
            res.NewBytes = newBytes;
            Log.Write("hotovo: " + res.Ok + " prevedeno, " + res.Failed
                    + " preskoceno, usetreno " + res.SavedMb + " MB");
            return res;
        }

        public static RunResult Restore(GameInfo g, Progress prog)
        {
            Log.Write("obnovení: " + g.BackupDir);
            prog(0, "Hledám zálohované soubory…");

            string[] files = Directory.GetFiles(g.BackupDir, "*.xnb", SearchOption.AllDirectories);
            RunResult res = new RunResult();
            res.Total = files.Length;

            for (int i = 0; i < files.Length; i++)
            {
                string rel = files[i].Substring(g.BackupDir.Length + 1);
                string dst = Path.Combine(g.ContentDir, rel);
                try
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(dst));
                    File.Copy(files[i], dst, true);
                    res.Ok++;
                }
                catch (Exception ex)
                {
                    res.Failed++;
                    Log.Write("nelze obnovit " + rel + ": " + ex.Message);
                }
                int d = i + 1;
                if ((d % 5) == 0 || d == res.Total)
                    prog((int)(95L * d / Math.Max(1, res.Total)),
                        "Obnovuji soubory…   " + d + " / " + res.Total);
            }

            prog(97, "Zapínám zpět debug mód…");
            RemoveIni(g);
            prog(100, "Hotovo.");
            Log.Write("obnoveno " + res.Ok + " z " + res.Total);
            return res;
        }
    }

    // --------------------------------------------------------------- UI

    public class MainForm : Form
    {
        private const string WarningText =
            "Tento program slouží k optimalizaci chodu hry Simt Simulator. " +
            "Spuštění optimalizace je na vlastní nebezpečí. " +
            "Program funguje na následujícím princpu: vypnutí debug módu, " +
            "komprimace vybraných textur a dogenerování minimap k některým texturám. " +
            "Očekávaným výsledkem by mělo být zlepšení rychlosti a odezvy hry. " +
            "Rozhodně si před provedením optimalizace vždy zazálohujte všechny soubory!";

        private const string DoNotInterrupt =
            "Nepřerušujte proces optimalizace, jinak hra nemusí fungovat!";

        private readonly Panel _body = new Panel();
        private readonly Label _title = new Label();
        private readonly Button _next = new Button();
        private readonly Button _back = new Button();
        private readonly Button _cancel = new Button();

        private GameInfo _game;
        private bool _restoreMode;
        private int _step;
        private bool _busy;

        private ProgressBar _bar;
        private Label _progressLabel;
        private BackgroundWorker _worker;
        private string _resultText;

        public MainForm()
        {
            Text = "Simt Optimizer";
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(560, 380);
            Font = new Font("Segoe UI", 9f);

            _title.SetBounds(16, 14, 528, 26);
            _title.Font = new Font("Segoe UI", 14f, FontStyle.Bold);
            Controls.Add(_title);

            _body.SetBounds(16, 48, 528, 262);
            Controls.Add(_body);

            _back.SetBounds(296, 330, 78, 30);
            _back.Text = "Zpět";
            _back.Click += OnBack;
            Controls.Add(_back);

            _next.SetBounds(382, 330, 78, 30);
            _next.Text = "Další";
            _next.Click += OnNext;
            Controls.Add(_next);

            _cancel.SetBounds(468, 330, 78, 30);
            _cancel.Text = "Zavřít";
            _cancel.Click += OnCancel;
            Controls.Add(_cancel);

            FormClosing += OnFormClosing;
            ShowStep(0);
        }

        // ------------------------------------------------------ kroky

        private void ShowStep(int step)
        {
            _step = step;
            _body.Controls.Clear();
            _back.Enabled = (step == 1 || step == 2);
            _next.Enabled = true;
            _next.Text = "Další";

            switch (step)
            {
                case 0: BuildWarning(); break;
                case 1: BuildFolder(); break;
                case 2: BuildConfirm(); break;
                case 3: BuildProgress(); break;
                case 4: BuildDone(); break;
            }
        }

        private Label AddText(string s, int y, int height)
        {
            Label l = new Label();
            l.SetBounds(0, y, 528, height);
            l.Text = s;
            _body.Controls.Add(l);
            return l;
        }

        // --- 1. upozornení

        private void BuildWarning()
        {
            _title.Text = "Upozornění";
            AddText(WarningText, 0, 150);
            Label l = AddText("Program není oficiální nástroj autora hry. Změny lze kdykoli vrátit "
                         + "tlačítkem, které se objeví při dalším spuštění.", 160, 44);
            l.ForeColor = Color.DimGray;
            _next.Text = "Rozumím";
        }

        // --- 2. výber slozky

        private TextBox _folderBox;
        private Label _folderStatus;

        private void BuildFolder()
        {
            _title.Text = "Výběr složky hry";
            AddText("Vyberte složku, ve které je nainstalovaná hra (obsahuje SimtSimulator.exe):", 0, 22);

            _folderBox = new TextBox();
            _folderBox.SetBounds(0, 40, 420, 34);
            _folderBox.ReadOnly = true;
            _body.Controls.Add(_folderBox);

            Button browse = new Button();
            browse.SetBounds(428, 37, 150, 36);
            browse.Text = "Procházet…";
            browse.Click += OnBrowse;
            _body.Controls.Add(browse);

            _folderStatus = AddText("", 64, 170);

            if (_game != null) { _folderBox.Text = _game.GameDir; Validate(_game.GameDir); }
            else _next.Enabled = false;
        }

        private void OnBrowse(object sender, EventArgs e)
        {
            using (FolderBrowserDialog d = new FolderBrowserDialog())
            {
                d.Description = "Vyberte složku hry Simt Simulator";
                d.ShowNewFolderButton = false;
                if (d.ShowDialog(this) != DialogResult.OK) return;
                _folderBox.Text = d.SelectedPath;
                Validate(d.SelectedPath);
            }
        }

        private void Validate(string dir)
        {
            string problem;
            _game = Core.Detect(dir, out problem);
            if (_game == null)
            {
                _folderStatus.ForeColor = Color.Firebrick;
                _folderStatus.Text = "Tohle nevypadá jako složka hry.\r\n\r\n" + problem;
                _next.Enabled = false;
                return;
            }

            _restoreMode = Core.BackupExists(_game);

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Hra nalezena.");
            sb.AppendLine();
            sb.AppendLine("Verze: " + _game.Version);
            if (_game.Version != Core.ExpectedVersion)
                sb.AppendLine("Pozor: nástroj byl ověřen na verzi " + Core.ExpectedVersion
                            + ". Na jiné verzi pokračujte obezřetně.");
            sb.AppendLine();
            if (_restoreMode)
            {
                sb.AppendLine("Tato instalace už je optimalizovaná — existuje záloha originálů:");
                sb.AppendLine(_game.BackupDir);
                sb.AppendLine();
                sb.AppendLine("Můžete originální soubory vrátit zpět.");
                _next.Text = "Vrátit zpět";
            }
            _folderStatus.ForeColor = Color.Black;
            _folderStatus.Text = sb.ToString();
            _next.Enabled = true;
        }

        // --- 3. potvrzení

        private void BuildConfirm()
        {
            _title.Text = _restoreMode ? "Potvrzení obnovení" : "Potvrzení optimalizace";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Složka hry:");
            sb.AppendLine("   " + _game.GameDir);
            sb.AppendLine();

            if (_restoreMode)
            {
                sb.AppendLine("Proběhne obnovení původních souborů ze zálohy:");
                sb.AppendLine("   " + _game.BackupDir);
                sb.AppendLine();
                sb.AppendLine("Zároveň se smažou soubory SimtSimulator.ini a SimtSimulator32.ini,");
                sb.AppendLine("tedy se znovu zapne debug mód hry.");
                sb.AppendLine();
                sb.AppendLine("Zálohu si program po obnovení ponechá; smazat ji můžete ručně.");
                _next.Text = "Obnovit";
            }
            else
            {
                sb.AppendLine("Proběhnou tyto kroky:");
                sb.AppendLine("   1. vypnutí debug módu hry");
                sb.AppendLine("   2. komprimace vybraných textur");
                sb.AppendLine("   3. dogenerování mipmap tam, kde chybí");
                sb.AppendLine();
                sb.AppendLine("Originály se předtím zkopírují do zálohy:");
                sb.AppendLine("   " + _game.BackupDir);
                sb.AppendLine();
                sb.AppendLine("Hra musí být zavřená. Optimalizace trvá jednotky minut.");
                _next.Text = "Optimalizovat";
            }

            AddText(sb.ToString(), 0, 230);
        }

        // --- 4. prubeh

        private void BuildProgress()
        {
            _title.Text = _restoreMode ? "Probíhá obnovení" : "Probíhá optimalizace";
            _back.Enabled = false;
            _next.Enabled = false;
            _cancel.Enabled = false;

            Label warn = AddText(DoNotInterrupt, 0, 30);
            warn.ForeColor = Color.Firebrick;
            warn.Font = new Font("Segoe UI", 10f, FontStyle.Bold);

            _bar = new ProgressBar();
            _bar.SetBounds(0, 48, 528, 26);
            _bar.Maximum = 100;
            _body.Controls.Add(_bar);

            _progressLabel = AddText("Připravuji…", 84, 140);

            _busy = true;
            _worker = new BackgroundWorker();
            _worker.WorkerReportsProgress = true;
            _worker.DoWork += WorkerDoWork;
            _worker.ProgressChanged += WorkerProgress;
            _worker.RunWorkerCompleted += WorkerDone;
            _worker.RunWorkerAsync();
        }

        private void WorkerProgress(object sender, ProgressChangedEventArgs e)
        {
            _bar.Value = Math.Max(0, Math.Min(100, e.ProgressPercentage));
            if (e.UserState != null) _progressLabel.Text = (string)e.UserState;
        }

        private void WorkerDoWork(object sender, DoWorkEventArgs e)
        {
            BackgroundWorker w = (BackgroundWorker)sender;
            Progress prog = delegate(int pct, string msg) { w.ReportProgress(pct, msg); };
            RunResult r = _restoreMode ? Core.Restore(_game, prog) : Core.Optimize(_game, prog);
            e.Result = _restoreMode ? FormatRestore(r) : FormatOptimize(r);
        }

        private void WorkerDone(object sender, RunWorkerCompletedEventArgs e)
        {
            _busy = false;
            _cancel.Enabled = true;
            if (e.Error != null)
            {
                Log.Write("CHYBA: " + e.Error);
                _resultText = "Došlo k chybě:\r\n\r\n" + e.Error.Message
                    + "\r\n\r\nZáloha originálů zůstala zachována, takže se lze vrátit zpět "
                    + "opětovným spuštěním programu.\r\n\r\nPodrobnosti: " + Log.Path_;
                _title.Text = "Chyba";
            }
            else
            {
                _resultText = (string)e.Result;
            }
            ShowStep(4);
        }

        // --- 5. hotovo

        private void BuildDone()
        {
            if (_title.Text != "Chyba")
                _title.Text = _restoreMode ? "Obnovení dokončeno" : "Optimalizace dokončena";
            AddText(_resultText, 0, 230);
            _back.Enabled = false;
            _next.Enabled = false;
            _cancel.Text = "Zavřít";
        }

        // -------------------------------------------------------- práce

        private string FormatOptimize(RunResult r)
        {
            if (r.NothingToDo)
                return "Nenašel jsem žádné textury k převedení — obsah hry už je "
                     + "pravděpodobně optimalizovaný.\r\n\r\nDebug mód byl vypnut.";

            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Optimalizace proběhla v pořádku.");
            sb.AppendLine();
            sb.AppendLine("Převedeno textur:   " + r.Ok);
            sb.AppendLine("Ušetřeno místa:     " + r.SavedMb + " MB");
            sb.AppendLine("Debug mód:          vypnut");
            if (r.Failed > 0)
                sb.AppendLine("Přeskočeno souborů: " + r.Failed + " (podrobnosti v logu)");
            sb.AppendLine();
            sb.AppendLine("Originály jsou uložené v:");
            sb.AppendLine("   " + _game.BackupDir);
            sb.AppendLine();
            sb.AppendLine("Zálohu si nechte, dokud si hru nevyzkoušíte. Vrátit vše zpět můžete");
            sb.AppendLine("opětovným spuštěním tohoto programu.");
            return sb.ToString();
        }

        private string FormatRestore(RunResult r)
        {
            StringBuilder sb = new StringBuilder();
            sb.AppendLine("Obnovení proběhlo v pořádku.");
            sb.AppendLine();
            sb.AppendLine("Obnoveno souborů: " + r.Ok + " z " + r.Total);
            if (r.Failed > 0) sb.AppendLine("Nepodařilo se:    " + r.Failed + " (podrobnosti v logu)");
            sb.AppendLine("Debug mód:        zapnut zpět");
            sb.AppendLine();
            sb.AppendLine("Hra je ve stavu před optimalizací. Zálohu můžete smazat ručně:");
            sb.AppendLine("   " + _game.BackupDir);
            return sb.ToString();
        }

        // ------------------------------------------------------ tlacítka

        private void OnNext(object sender, EventArgs e)
        {
            if (_step == 2)
            {
                if (!_restoreMode && Core.IsGameRunning())
                {
                    MessageBox.Show(this, "Hra právě běží. Zavřete ji a zkuste to znovu.",
                        "Hra běží", MessageBoxButtons.OK, MessageBoxIcon.Warning);
                    return;
                }
                ShowStep(3);
                return;
            }
            if (_step < 3) ShowStep(_step + 1);
        }

        private void OnBack(object sender, EventArgs e)
        {
            if (_step > 0) ShowStep(_step - 1);
        }

        private void OnCancel(object sender, EventArgs e)
        {
            Close();
        }

        private void OnFormClosing(object sender, FormClosingEventArgs e)
        {
            if (!_busy) return;
            e.Cancel = true;
            MessageBox.Show(this, DoNotInterrupt, "Probíhá optimalizace",
                MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
    }

    // ---------------------------------------------------------- vstupní bod

    public static class Program
    {
        // baseDir predává volající skript — pod PowerShellem je BaseDirectory
        // slozka PowerShellu, ne slozka programu.
        public static void Run(string baseDir)
        {
            try { Log.Start(baseDir); }
            catch { }
            Application.EnableVisualStyles();
            Application.SetCompatibleTextRenderingDefault(false);
            Application.Run(new MainForm());
        }
    }
}
