using System.Security.Cryptography;
using System.Text;
using Microsoft.Data.Sqlite;

namespace QqDb;

internal static class Program
{
    private const int HeaderSize = 1024;

    private static int Main(string[] args)
    {
        Console.OutputEncoding = Encoding.UTF8;
        try
        {
            return (args.Length > 0 ? args[0] : "help") switch
            {
                "key" => Key(),
                "probe" => Probe(),
                "schema" => Schema(args.Skip(1).ToArray()),
                "scope" => Scope(args.Skip(1).ToArray()),
                _ => Help(),
            };
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("ERROR: " + ex.Message);
            return 1;
        }
    }

    private static int Help()
    {
        Console.WriteLine("""
            qq-db <command>
              key                显示从 Registry20 解出的 DB 密钥（16 字节）
              schema <db>        打印解密后的表结构
              scope [--out p]    从 nt_msg.db 生成 scope-map.json（会话标题 -> 群号/c2c）
            """);
        return 2;
    }

    private static string TencentRoot =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments), "Tencent Files");

    private static string DbDir => Path.Combine(TencentRoot, "1000000001", "nt_qq", "nt_db");   // 占位：改成你自己的 QQ 号

    public static string ReadKey()
    {
        var reg = Path.Combine(TencentRoot, "nt_qq", "global", "nt_data", "msf", "Registry20");
        var raw = Dpapi.Unprotect(File.ReadAllBytes(reg));
        if (raw.Length < 20) throw new InvalidOperationException("Registry20 解密结果过短");
        var guid = new byte[16];
        for (int i = 0; i < 16; i++) guid[i] = (byte)(~(raw[4 + i] ^ 16) & 0xFF);
        return Convert.ToHexString(guid).ToLowerInvariant();
    }

    private static int Key()
    {
        Console.WriteLine(ReadKey());
        return 0;
    }

    /// <summary>剥掉 1024 字节头，复制一份可被 SQLCipher 打开的文件。</summary>
    private static string Prepare(string dbPath)
    {
        var tmp = Path.Combine(Path.GetTempPath(), "qqdb", Path.GetFileName(dbPath));
        Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);

        // QQ 独占着数据库，必须用 ReadWrite 共享方式读，否则会被拒绝
        using var fs = new FileStream(dbPath, FileMode.Open, FileAccess.Read,
            FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        var data = ms.ToArray();
        File.WriteAllBytes(tmp, data.AsSpan(HeaderSize).ToArray());
        return tmp;
    }

    private static SqliteConnection Open(string dbPath, string key)
    {
        SQLitePCL.Batteries_V2.Init();
        var file = Prepare(dbPath);
        var conn = new SqliteConnection($"Data Source={file};Mode=ReadOnly;Cache=Private");
        conn.Open();
        using var cmd = conn.CreateCommand();
        // 必须和首个查询写在同一个 Command 里，否则 PRAGMA 可能未生效
        cmd.CommandText = $"""
            PRAGMA cipher_page_size = 4096;
            PRAGMA key = "x'{key}'";
            PRAGMA kdf_iter = 4000;
            PRAGMA cipher_hmac_algorithm = HMAC_SHA1;
            PRAGMA cipher_kdf_algorithm = PBKDF2_HMAC_SHA512;
            SELECT count(*) FROM sqlite_master;
            """;
        cmd.ExecuteScalar();   // 密钥错会在这里抛
        return conn;
    }

    private static int Probe()
    {
        SQLitePCL.Batteries_V2.Init();
        var key = ReadKey();
        var dbPath = Path.Combine(DbDir, "nt_msg.db");

        using var fs = new FileStream(dbPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        using var ms = new MemoryStream();
        fs.CopyTo(ms);
        var raw = ms.ToArray();

        foreach (var header in new[] { 1024, 0 })
        {
            var body = raw.AsSpan(header).ToArray();
            var tmp = Path.Combine(Path.GetTempPath(), "qqdb", $"probe_{header}.db");
            Directory.CreateDirectory(Path.GetDirectoryName(tmp)!);
            File.WriteAllBytes(tmp, body);

            foreach (var keyMode in new[] { "raw", "text" })
            {
                var keyClause = keyMode == "raw" ? $"PRAGMA key = \"x'{key}'\";" : $"PRAGMA key = '{key}';";
                foreach (var iter in new[] { 4000, 64000, 256000 })
                {
                    try
                    {
                        using var conn = new SqliteConnection($"Data Source={tmp};Mode=ReadOnly;Cache=Private");
                        conn.Open();
                        foreach (var sql in new[]
                                 {
                                     "PRAGMA cipher_page_size = 4096;",
                                     keyClause,
                                     $"PRAGMA kdf_iter = {iter};",
                                     "PRAGMA cipher_hmac_algorithm = HMAC_SHA1;",
                                     "PRAGMA cipher_kdf_algorithm = PBKDF2_HMAC_SHA512;",
                                 })
                        {
                            using var p = conn.CreateCommand();
                            p.CommandText = sql;
                            p.ExecuteNonQuery();
                        }
                        using var check = conn.CreateCommand();
                        check.CommandText = "SELECT count(*) FROM sqlite_master;";
                        var n = check.ExecuteScalar();
                        Console.WriteLine($"OK header={header} key={keyMode} iter={iter} tables={n}");
                        return 0;
                    }
                    catch (Exception ex)
                    {
                        Console.WriteLine($"x  header={header} key={keyMode} iter={iter}: {ex.Message.Split('\n')[0]}");
                    }
                }
            }
        }
        return 1;
    }

    private static int Schema(string[] args)
    {
        var name = args.Length > 0 ? args[0] : "nt_msg.db";
        var path = Path.IsPathRooted(name) ? name : Path.Combine(DbDir, name);
        using var conn = Open(path, ReadKey());

        using var tables = conn.CreateCommand();
        tables.CommandText = "SELECT name FROM sqlite_master WHERE type='table' ORDER BY name;";
        using var r = tables.ExecuteReader();
        var names = new List<string>();
        while (r.Read()) names.Add(r.GetString(0));
        r.Close();

        Console.WriteLine($"{Path.GetFileName(path)}: {names.Count} tables");
        foreach (var t in names)
        {
            using var c = conn.CreateCommand();
            c.CommandText = $"SELECT count(*) FROM [{t}];";
            long n;
            try { n = Convert.ToInt64(c.ExecuteScalar()); } catch { n = -1; }
            Console.WriteLine($"  {t}: {n}");
        }
        return 0;
    }

    private static int Scope(string[] args)
    {
        var outPath = "data/scope-map.json";
        for (int i = 0; i < args.Length - 1; i++)
            if (args[i] == "--out") outPath = args[i + 1];

        var key = ReadKey();
        var map = new Dictionary<string, object>();

        // 1) 群：group_msg_table 里的 group id + group_info 里的群名
        var names = ReadGroupNames(key);
        foreach (var (id, count) in ReadGroupIds(key))
        {
            var title = names.TryGetValue(id, out var nm) && !string.IsNullOrWhiteSpace(nm) ? nm : id.ToString();
            map[title] = new { kind = "group", id };
            Console.WriteLine($"group {id} -> '{title}' ({count} msgs)");
        }

        // 2) 私聊：c2c_msg_table 的 uid/qq
        foreach (var (id, count) in ReadC2cIds(key))
        {
            map[id.ToString()] = new { kind = "c2c", id };
            Console.WriteLine($"c2c {id} ({count} msgs)");
        }

        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(outPath))!);
        File.WriteAllText(Path.GetFullPath(outPath),
            System.Text.Json.JsonSerializer.Serialize(map,
                new System.Text.Json.JsonSerializerOptions { WriteIndented = true }));
        Console.WriteLine($"written {Path.GetFullPath(outPath)} ({map.Count})");
        return 0;
    }

    private static Dictionary<long, string> ReadGroupNames(string key)
    {
        var result = new Dictionary<long, string>();
        try
        {
            using var conn = Open(Path.Combine(DbDir, "group_info.db"), key);
            using var cmd = conn.CreateCommand();
            cmd.CommandText = "SELECT name FROM sqlite_master WHERE type='table';";
            using var r = cmd.ExecuteReader();
            var tables = new List<string>();
            while (r.Read()) tables.Add(r.GetString(0));
            r.Close();

            foreach (var t in tables)
            {
                try
                {
                    using var q = conn.CreateCommand();
                    q.CommandText = $"SELECT * FROM [{t}] LIMIT 200;";
                    using var rr = q.ExecuteReader();
                    var cols = new List<string>();
                    for (int i = 0; i < rr.FieldCount; i++) cols.Add(rr.GetName(i));
                    while (rr.Read())
                    {
                        long? id = null;
                        string? name = null;
                        for (int i = 0; i < cols.Count; i++)
                        {
                            if (rr.IsDBNull(i)) continue;
                            var col = cols[i].ToLowerInvariant();
                            if (id is null && (col.Contains("group") && col.Contains("code") || col == "40021" || col.Contains("group_id")))
                            {
                                if (long.TryParse(rr.GetValue(i)?.ToString(), out var v)) id = v;
                            }
                            if (name is null && (col.Contains("name") || col == "40031"))
                                name = rr.GetValue(i)?.ToString();
                        }
                        if (id is not null && !string.IsNullOrWhiteSpace(name)) result[id.Value] = name!;
                    }
                }
                catch { }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine("group_info 读取失败: " + ex.Message);
        }
        return result;
    }

    private static List<(long Id, long Count)> ReadGroupIds(string key)
    {
        var list = new List<(long, long)>();
        using var conn = Open(Path.Combine(DbDir, "nt_msg.db"), key);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 40021, count(*) FROM group_msg_table GROUP BY 40021 ORDER BY 2 DESC;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0)) continue;
            if (long.TryParse(r.GetValue(0)?.ToString(), out var id)) list.Add((id, r.GetInt64(1)));
        }
        return list;
    }

    private static List<(long Id, long Count)> ReadC2cIds(string key)
    {
        var list = new List<(long, long)>();
        using var conn = Open(Path.Combine(DbDir, "nt_msg.db"), key);
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SELECT 40030, count(*) FROM c2c_msg_table WHERE 40030 IS NOT NULL GROUP BY 40030 ORDER BY 2 DESC LIMIT 30;";
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            if (r.IsDBNull(0)) continue;
            if (long.TryParse(r.GetValue(0)?.ToString(), out var id)) list.Add((id, r.GetInt64(1)));
        }
        return list;
    }
}

internal static class Dpapi
{
    [System.Runtime.InteropServices.DllImport("crypt32.dll", SetLastError = true)]
    private static extern bool CryptUnprotectData(ref DATA_BLOB pDataIn, IntPtr ppszDataDescr,
        IntPtr pOptionalEntropy, IntPtr pvReserved, IntPtr pPromptStruct, uint dwFlags, ref DATA_BLOB pDataOut);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr LocalFree(IntPtr hMem);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct DATA_BLOB
    {
        public int cbData;
        public IntPtr pbData;
    }

    public static byte[] Unprotect(byte[] data)
    {
        var input = new DATA_BLOB { cbData = data.Length, pbData = System.Runtime.InteropServices.Marshal.AllocHGlobal(data.Length) };
        System.Runtime.InteropServices.Marshal.Copy(data, 0, input.pbData, data.Length);
        var output = new DATA_BLOB();
        try
        {
            if (!CryptUnprotectData(ref input, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, IntPtr.Zero, 0, ref output))
                throw new InvalidOperationException("CryptUnprotectData 失败");
            var result = new byte[output.cbData];
            System.Runtime.InteropServices.Marshal.Copy(output.pbData, result, 0, output.cbData);
            return result;
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(input.pbData);
            if (output.pbData != IntPtr.Zero) LocalFree(output.pbData);
        }
    }
}
