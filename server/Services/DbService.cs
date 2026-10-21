using Microsoft.Data.Sqlite;
using VoiceVault.Server.Models;

namespace VoiceVault.Server.Services;

/// <summary>
/// SQLite 元数据存取。录音文件本身落盘，元数据（含转写状态）存库。
/// 去重键：(device, sha256)。
/// </summary>
public class DbService
{
    private readonly string _conn;

    public DbService(string dataDir)
    {
        Directory.CreateDirectory(dataDir);
        _conn = $"Data Source={Path.Combine(dataDir, "voicvault.db")}";

        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            CREATE TABLE IF NOT EXISTS recordings (
                id INTEGER PRIMARY KEY AUTOINCREMENT,
                device TEXT,
                rel_path TEXT,
                file_name TEXT,
                stored_path TEXT,
                size INTEGER,
                mtime INTEGER,
                sha256 TEXT,
                title TEXT,
                recorded_at INTEGER,
                transcript_status TEXT,
                transcript_path TEXT,
                language TEXT,
                created_at TEXT
            );
            CREATE UNIQUE INDEX IF NOT EXISTS idx_dev_sha ON recordings(device, sha256);
            CREATE INDEX IF NOT EXISTS idx_recorded_at ON recordings(recorded_at DESC);
            """;
        cmd.ExecuteNonQuery();

        // 增量列迁移（已存在时静默跳过）
        foreach (var ddl in new[]
        {
            "ALTER TABLE recordings ADD COLUMN transcript_text TEXT",           // 文字稿全文（供检索）
            "ALTER TABLE recordings ADD COLUMN duration_sec REAL",              // 录音时长（秒）
            "ALTER TABLE recordings ADD COLUMN transcript_attempts INTEGER NOT NULL DEFAULT 0", // 转写失败重试计数
            "ALTER TABLE recordings ADD COLUMN note TEXT",                      // 用户备注
        })
        {
            try
            {
                using var mi = c.CreateCommand();
                mi.CommandText = ddl;
                mi.ExecuteNonQuery();
            }
            catch (SqliteException) { /* 列已存在 */ }
        }
    }

    /// <summary>打开连接并应用并发/崩溃安全 PRAGMA（WAL + 同步 + 忙等待）。</summary>
    private SqliteConnection OpenConn()
    {
        var c = new SqliteConnection(_conn);
        c.Open();
        using var p = c.CreateCommand();
        // WAL：写不阻塞读，崩溃恢复更安全；NORMAL 同步在 WAL 下足够安全且更快；
        // busy_timeout：并发读写（上传写 + 转写写 + 列表读）时等待而非立即报 locked。
        p.CommandText = "PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
        p.ExecuteNonQuery();
        return c;
    }

    public bool ExistsBySha(string device, string sha)
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT 1 FROM recordings WHERE device=@d AND sha256=@s LIMIT 1";
        cmd.Parameters.AddWithValue("@d", device);
        cmd.Parameters.AddWithValue("@s", sha);
        return cmd.ExecuteScalar() != null;
    }

    public long Upsert(Recording r)
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            INSERT INTO recordings
                (device, rel_path, file_name, stored_path, size, mtime, sha256, title,
                 recorded_at, transcript_status, transcript_path, language, created_at)
            VALUES
                (@device, @rel, @fn, @sp, @size, @mtime, @sha, @title,
                 @ra, @ts, @tp, @lang, @ca)
            ON CONFLICT(device, sha256) DO UPDATE SET
                rel_path = excluded.rel_path,
                mtime   = excluded.mtime,
                title   = excluded.title;
            """;
        cmd.Parameters.AddWithValue("@device", r.Device);
        cmd.Parameters.AddWithValue("@rel", r.RelPath);
        cmd.Parameters.AddWithValue("@fn", r.FileName);
        cmd.Parameters.AddWithValue("@sp", r.StoredPath);
        cmd.Parameters.AddWithValue("@size", r.Size);
        cmd.Parameters.AddWithValue("@mtime", r.Mtime);
        cmd.Parameters.AddWithValue("@sha", r.Sha256);
        cmd.Parameters.AddWithValue("@title", (object?)r.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ra", r.RecordedAt);
        cmd.Parameters.AddWithValue("@ts", (object?)r.TranscriptStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tp", (object?)r.TranscriptPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lang", (object?)r.Language ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ca", r.CreatedAt.ToString("o"));
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT id FROM recordings WHERE device=@device AND sha256=@sha";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    public Recording? GetById(long id)
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {ListColumns} FROM recordings WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        using var r = cmd.ExecuteReader();
        return r.Read() ? ReadRecording(r) : null;
    }

    public List<Recording> List(int page, int pageSize, long from, long to, string? q)
    {
        var items = new List<Recording>();
        using var c = OpenConn();
        using var cmd = c.CreateCommand();

        var where = BuildWhere(from, to, q, cmd);
        cmd.CommandText = $"""
            SELECT {ListColumns} FROM recordings {where}
            ORDER BY recorded_at DESC, id DESC
            LIMIT @limit OFFSET @offset
            """;
        cmd.Parameters.AddWithValue("@limit", pageSize);
        cmd.Parameters.AddWithValue("@offset", (page - 1) * pageSize);
        using var r = cmd.ExecuteReader();
        while (r.Read()) items.Add(ReadRecording(r));
        return items;
    }

    public long Count(long from, long to, string? q)
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        var where = BuildWhere(from, to, q, cmd);
        cmd.CommandText = $"SELECT COUNT(*) FROM recordings {where}";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>
    /// 列表/详情查询列。刻意不含 transcript_text（大字段）：
    /// 全文仅供 LIKE 检索，正文始终走 /transcript 端点，避免列表接口膨胀。
    /// </summary>
    private const string ListColumns =
        "id, device, rel_path, file_name, stored_path, size, mtime, sha256, title, recorded_at, " +
        "transcript_status, transcript_path, language, duration_sec, transcript_attempts, note, created_at";

    /// <summary>
    /// 闲时调度器取数：待闲时（idle）的录音 + 转写失败未达重试上限
    /// （error 且 attempts &lt; 3，即初失败后最多自动重试 2 次）的录音。
    /// </summary>
    public List<long> GetIdlePendingIds()
    {
        var ids = new List<long>();
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM recordings
            WHERE transcript_status = 'idle'
               OR (transcript_status = 'error' AND transcript_attempts < 3)
            ORDER BY id
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    /// <summary>返回所有未完成转写的录音 id（none / idle / error），供「全部转写」批量入队。</summary>
    public List<long> GetTranscribableIds()
    {
        var ids = new List<long>();
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM recordings
            WHERE transcript_status IS NULL OR transcript_status NOT IN ('done','pending')
            ORDER BY id
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    /// <summary>删除一条录音记录并返回其元数据（调用方负责删文件）；不存在返回 null。</summary>
    public Recording? DeleteById(long id)
    {
        var rec = GetById(id);
        if (rec is null) return null;
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "DELETE FROM recordings WHERE id=@id";
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
        return rec;
    }

    public void UpdateTranscript(long id, string path, string status, string? lang, int attemptsDelta = 0)
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE recordings
            SET transcript_status=@ts, transcript_path=@tp, language=@lang,
                transcript_attempts = MAX(0, transcript_attempts + @delta)
            WHERE id=@id
            """;
        cmd.Parameters.AddWithValue("@ts", status);
        cmd.Parameters.AddWithValue("@tp", path);
        cmd.Parameters.AddWithValue("@lang", (object?)lang ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@delta", attemptsDelta);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>转写成功后落库全文（供检索）与时长（秒）。</summary>
    public void SaveTranscriptResult(long id, string plainText, double durationSec)
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE recordings SET transcript_text=@t, duration_sec=@d WHERE id=@id
            """;
        cmd.Parameters.AddWithValue("@t", plainText);
        cmd.Parameters.AddWithValue("@d", durationSec);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>更新标题/备注：传 null 表示不修改，空字符串表示清空。</summary>
    public void UpdateMeta(long id, string? title, string? note)
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE recordings SET title=COALESCE(@title, title), note=COALESCE(@note, note)
            WHERE id=@id
            """;
        cmd.Parameters.AddWithValue("@title", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>保留策略：早于 cutoff（Unix 秒）的录音 id，由旧到新。</summary>
    public List<long> GetIdsOlderThan(long cutoff)
    {
        var ids = new List<long>();
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM recordings WHERE recorded_at < @c ORDER BY recorded_at ASC, id ASC";
        cmd.Parameters.AddWithValue("@c", cutoff);
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    /// <summary>全部录音占用字节数（保留策略容量判断用）。</summary>
    public long TotalBytes()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT COALESCE(SUM(size), 0) FROM recordings";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>最旧一条录音的 id（容量策略逐条删除用）；空库返回 null。</summary>
    public long? GetOldestId()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id FROM recordings ORDER BY recorded_at ASC, id ASC LIMIT 1";
        var v = cmd.ExecuteScalar();
        return v is null ? null : Convert.ToInt64(v);
    }

    private static string BuildWhere(long from, long to, string? q, SqliteCommand cmd)
    {
        var clauses = new List<string>();
        if (from > 0)
        {
            clauses.Add("recorded_at >= @from");
            cmd.Parameters.AddWithValue("@from", from);
        }
        if (to > 0)
        {
            clauses.Add("recorded_at <= @to");
            cmd.Parameters.AddWithValue("@to", to);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            // 关键词同时命中 文件名/标题/路径/备注/文字稿全文
            clauses.Add("(file_name LIKE @q OR title LIKE @q OR rel_path LIKE @q OR note LIKE @q OR transcript_text LIKE @q)");
            cmd.Parameters.AddWithValue("@q", $"%{q}%");
        }
        return clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : "";
    }

    private static Recording ReadRecording(SqliteDataReader r)
    {
        return new Recording
        {
            Id = r.GetInt64(r.GetOrdinal("id")),
            Device = r.GetString(r.GetOrdinal("device")),
            RelPath = r.GetString(r.GetOrdinal("rel_path")),
            FileName = r.GetString(r.GetOrdinal("file_name")),
            StoredPath = r.GetString(r.GetOrdinal("stored_path")),
            Size = r.GetInt64(r.GetOrdinal("size")),
            Mtime = r.GetInt64(r.GetOrdinal("mtime")),
            Sha256 = r.GetString(r.GetOrdinal("sha256")),
            Title = r.IsDBNull(r.GetOrdinal("title")) ? null : r.GetString(r.GetOrdinal("title")),
            RecordedAt = r.GetInt64(r.GetOrdinal("recorded_at")),
            TranscriptStatus = r.IsDBNull(r.GetOrdinal("transcript_status")) ? "none" : r.GetString(r.GetOrdinal("transcript_status")),
            TranscriptPath = r.IsDBNull(r.GetOrdinal("transcript_path")) ? null : r.GetString(r.GetOrdinal("transcript_path")),
            Language = r.IsDBNull(r.GetOrdinal("language")) ? null : r.GetString(r.GetOrdinal("language")),
            DurationSec = r.IsDBNull(r.GetOrdinal("duration_sec")) ? null : r.GetDouble(r.GetOrdinal("duration_sec")),
            TranscriptAttempts = r.GetInt32(r.GetOrdinal("transcript_attempts")),
            Note = r.IsDBNull(r.GetOrdinal("note")) ? null : r.GetString(r.GetOrdinal("note")),
            CreatedAt = DateTime.TryParse(r.GetString(r.GetOrdinal("created_at")), out var d) ? d : DateTime.MinValue
        };
    }
}
