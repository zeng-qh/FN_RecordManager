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

        // journal_mode=WAL 是数据库级持久属性，写进库文件后对所有连接生效，
        // 只需设置一次；无需每次开连接都执行（减少每查询固定开销）。
        using (var wal = OpenConn())
        using (var walCmd = wal.CreateCommand())
        {
            walCmd.CommandText = "PRAGMA journal_mode=WAL;";
            walCmd.ExecuteNonQuery();
        }

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
            CREATE INDEX IF NOT EXISTS idx_transcript_status ON recordings(transcript_status);
            """;
        cmd.ExecuteNonQuery();

        // 增量列迁移（已存在时静默跳过）
        foreach (var ddl in new[]
        {
            "ALTER TABLE recordings ADD COLUMN transcript_text TEXT",           // 文字稿全文（供检索）
            "ALTER TABLE recordings ADD COLUMN duration_sec REAL",              // 录音时长（秒）
            "ALTER TABLE recordings ADD COLUMN transcript_attempts INTEGER NOT NULL DEFAULT 0", // 转写失败重试计数
            "ALTER TABLE recordings ADD COLUMN note TEXT",                      // 用户备注
            "ALTER TABLE recordings ADD COLUMN transcript_progress REAL NOT NULL DEFAULT 0",    // 转写进度 0-100
            "ALTER TABLE recordings ADD COLUMN transcript_partial TEXT",        // 转写进行中的部分文本（流式预览）
            "ALTER TABLE recordings ADD COLUMN contact TEXT",                   // 联系人/来源分类键（电话号码或微信联系人）
            "ALTER TABLE recordings ADD COLUMN contact_kind TEXT",              // 分类类型：phone / wechat
            "ALTER TABLE recordings ADD COLUMN no_transcribe INTEGER NOT NULL DEFAULT 0", // 「默认不转写」标记（批量转写跳过）
            "ALTER TABLE recordings ADD COLUMN transcribe_ms INTEGER",          // 最近一次转写实际耗时（毫秒）
            "ALTER TABLE recordings ADD COLUMN transcribe_started_at INTEGER",  // 最近一次转写开始时间（Unix 秒）
            "ALTER TABLE recordings ADD COLUMN speech_sec REAL",                // 有声音时长（VAD 过滤静音后的语音时长）
            // 依赖新增列的索引必须在加列之后创建
            "CREATE INDEX IF NOT EXISTS idx_contact ON recordings(contact)",
            "CREATE INDEX IF NOT EXISTS idx_no_transcribe ON recordings(no_transcribe)",
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

    /// <summary>
    /// 打开连接并应用连接级 PRAGMA（同步策略 + 忙等待）。
    /// WAL 已在构造器一次性设置（数据库级持久属性），此处不再重复。
    /// </summary>
    private SqliteConnection OpenConn()
    {
        var c = new SqliteConnection(_conn);
        c.Open();
        using var p = c.CreateCommand();
        // NORMAL 同步在 WAL 下足够安全且更快；
        // busy_timeout：并发读写（上传写 + 转写写 + 列表读）时等待而非立即报 locked。
        p.CommandText = "PRAGMA synchronous=NORMAL; PRAGMA busy_timeout=5000;";
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

    /// <summary>
    /// 批量探测已存在的文件哈希（不限设备）：网页端上传前预校验用，
    /// 命中即「秒传」跳过上传。返回库中实际存在的 sha 子集。
    /// </summary>
    public List<string> ExistingShas(List<string> shas)
    {
        var found = new List<string>();
        if (shas.Count == 0) return found;
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        var names = new List<string>();
        for (var i = 0; i < shas.Count; i++)
        {
            names.Add($"@s{i}");
            cmd.Parameters.AddWithValue($"@s{i}", shas[i]);
        }
        cmd.CommandText = $"SELECT DISTINCT sha256 FROM recordings WHERE sha256 IN ({string.Join(",", names)})";
        using var r = cmd.ExecuteReader();
        while (r.Read()) found.Add(r.GetString(0));
        return found;
    }

    /// <summary>
    /// 插入或去重命中更新。返回 (id, isNew)：isNew=false 表示 (device, sha256)
    /// 命中已有记录（调用方可据此跳过重复转写、在上传响应中告知客户端）。
    /// </summary>
    public (long Id, bool IsNew) Upsert(Recording r)
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();

        // 先探测是否命中已有记录：ON CONFLICT DO UPDATE 无法区分插入与更新
        cmd.CommandText = "SELECT id FROM recordings WHERE device=@device AND sha256=@sha";
        cmd.Parameters.AddWithValue("@device", r.Device);
        cmd.Parameters.AddWithValue("@sha", r.Sha256);
        var existing = cmd.ExecuteScalar();
        if (existing is not null)
            return (Convert.ToInt64(existing), false);

        cmd.CommandText = """
            INSERT INTO recordings
                (device, rel_path, file_name, stored_path, size, mtime, sha256, title,
                 recorded_at, transcript_status, transcript_path, language, created_at,
                 contact, contact_kind)
            VALUES
                (@device, @rel, @fn, @sp, @size, @mtime, @sha, @title,
                 @ra, @ts, @tp, @lang, @ca, @contact, @ckind)
            ON CONFLICT(device, sha256) DO UPDATE SET
                rel_path = excluded.rel_path,
                mtime   = excluded.mtime,
                title   = excluded.title;
            """;
        cmd.Parameters.AddWithValue("@rel", r.RelPath);
        cmd.Parameters.AddWithValue("@fn", r.FileName);
        cmd.Parameters.AddWithValue("@sp", r.StoredPath);
        cmd.Parameters.AddWithValue("@size", r.Size);
        cmd.Parameters.AddWithValue("@mtime", r.Mtime);
        cmd.Parameters.AddWithValue("@title", (object?)r.Title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ra", r.RecordedAt);
        cmd.Parameters.AddWithValue("@ts", (object?)r.TranscriptStatus ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@tp", (object?)r.TranscriptPath ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@lang", (object?)r.Language ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ca", r.CreatedAt.ToString("o"));
        cmd.Parameters.AddWithValue("@contact", (object?)r.Contact ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ckind", (object?)r.ContactKind ?? DBNull.Value);
        cmd.ExecuteNonQuery();

        cmd.CommandText = "SELECT id FROM recordings WHERE device=@device AND sha256=@sha";
        return (Convert.ToInt64(cmd.ExecuteScalar()), true);
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

    public List<Recording> List(int page, int pageSize, long from, long to, string? q, string? contact = null)
    {
        var items = new List<Recording>();
        using var c = OpenConn();
        using var cmd = c.CreateCommand();

        var where = BuildWhere(from, to, q, contact, cmd);
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

    public long Count(long from, long to, string? q, string? contact = null)
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        var where = BuildWhere(from, to, q, contact, cmd);
        cmd.CommandText = $"SELECT COUNT(*) FROM recordings {where}";
        return Convert.ToInt64(cmd.ExecuteScalar());
    }

    /// <summary>
    /// 列表/详情查询列。刻意不含 transcript_text（大字段）：
    /// 全文仅供 LIKE 检索，正文始终走 /transcript 端点，避免列表接口膨胀。
    /// </summary>
    private const string ListColumns =
        "id, device, rel_path, file_name, stored_path, size, mtime, sha256, title, recorded_at, " +
        "transcript_status, transcript_path, language, duration_sec, speech_sec, transcript_attempts, " +
        "transcript_progress, transcript_partial, note, contact, contact_kind, created_at, " +
        "no_transcribe, transcribe_ms, transcribe_started_at";

    /// <summary>
    /// 闲时调度器取数：待闲时（idle）的录音 + 转写失败未达重试上限
    /// （error 且 attempts &lt; 3，即初失败后最多自动重试 2 次）的录音。
    /// 「默认不转写」标记的记录不参与自动调度。
    /// </summary>
    public List<long> GetIdlePendingIds()
    {
        var ids = new List<long>();
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM recordings
            WHERE COALESCE(no_transcribe, 0) = 0
              AND (transcript_status = 'idle'
               OR (transcript_status = 'error' AND transcript_attempts < 3))
            ORDER BY id
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    /// <summary>
    /// 返回所有未完成转写的录音 id（none / idle / error），供「一键转写」批量入队。
    /// 排除已转写（done）、进行中（pending）与「默认不转写」标记的记录。
    /// </summary>
    public List<long> GetTranscribableIds()
    {
        var ids = new List<long>();
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT id FROM recordings
            WHERE COALESCE(no_transcribe, 0) = 0
              AND (transcript_status IS NULL OR transcript_status NOT IN ('done','pending'))
            ORDER BY id
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read()) ids.Add(r.GetInt64(0));
        return ids;
    }

    /// <summary>批量设置「默认不转写」标记，返回实际更新的行数。标记时同步把 idle 状态回置 none（不再被闲时调度）。</summary>
    public int SetNoTranscribe(List<long> ids, bool value)
    {
        if (ids.Count == 0) return 0;
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        var names = new List<string>();
        for (var i = 0; i < ids.Count; i++)
        {
            names.Add($"@id{i}");
            cmd.Parameters.AddWithValue($"@id{i}", ids[i]);
        }
        cmd.Parameters.AddWithValue("@v", value ? 1 : 0);
        cmd.CommandText =
            $"UPDATE recordings SET no_transcribe=@v WHERE id IN ({string.Join(",", names)})";
        var n = cmd.ExecuteNonQuery();

        // 标记为不转写的记录若正处于「待闲时」状态，回置为未转写（闲时调度已排除 no_transcribe）
        if (value)
        {
            cmd.Parameters.Clear();
            for (var i = 0; i < ids.Count; i++)
                cmd.Parameters.AddWithValue($"@id{i}", ids[i]);
            cmd.CommandText =
                $"UPDATE recordings SET transcript_status='none' WHERE transcript_status='idle' AND id IN ({string.Join(",", names)})";
            cmd.ExecuteNonQuery();
        }
        return n;
    }

    /// <summary>转写开始：记录起始时间（前端「已进行」计时用）。</summary>
    public void SetTranscribeStarted(long id, long unixSec)
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE recordings SET transcribe_started_at=@t WHERE id=@id";
        cmd.Parameters.AddWithValue("@t", unixSec);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>转写收尾：记录本次实际耗时（毫秒，成功与失败均记录，供速度估算与展示）。</summary>
    public void SetTranscribeElapsed(long id, long ms)
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE recordings SET transcribe_ms=@ms WHERE id=@id";
        cmd.Parameters.AddWithValue("@ms", Math.Max(0, ms));
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 按转写视角列出录音（转写管理弹窗）。status：todo 待转写（含失败）/
    /// pending 转写中 / done 已转写 / skip 已标记不转写 / all 全部。
    /// </summary>
    public List<Recording> ListForTranscribe(string status)
    {
        var where = status switch
        {
            "todo" => "WHERE COALESCE(no_transcribe,0)=0 AND (transcript_status IS NULL OR transcript_status IN ('none','idle','error'))",
            "pending" => "WHERE transcript_status='pending'",
            "done" => "WHERE transcript_status='done'",
            "skip" => "WHERE COALESCE(no_transcribe,0)=1",
            _ => "",
        };
        var items = new List<Recording>();
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = $"SELECT {ListColumns} FROM recordings {where} ORDER BY recorded_at DESC, id DESC";
        using var r = cmd.ExecuteReader();
        while (r.Read()) items.Add(ReadRecording(r));
        return items;
    }

    /// <summary>
    /// 转写统计计数（弹窗顶部条）：
    /// todo 待转写（未标记，含失败/待闲时）、pending 转写中、done 已转写、
    /// error 失败（未标记）、skipped 已标记不转写；todoDurationSec 待转写总时长（估算预计耗时用）。
    /// </summary>
    public (int Todo, int Pending, int Done, int Error, int Skipped, double TodoDurationSec) TranscribeCounts()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT
              SUM(CASE WHEN COALESCE(no_transcribe,0)=0 AND (transcript_status IS NULL OR transcript_status IN ('none','idle','error')) THEN 1 ELSE 0 END),
              SUM(CASE WHEN transcript_status='pending' THEN 1 ELSE 0 END),
              SUM(CASE WHEN transcript_status='done' THEN 1 ELSE 0 END),
              SUM(CASE WHEN transcript_status='error' AND COALESCE(no_transcribe,0)=0 THEN 1 ELSE 0 END),
              SUM(CASE WHEN COALESCE(no_transcribe,0)=1 THEN 1 ELSE 0 END),
              -- 估算预计耗时按「有声音时长」累计（转写耗时正比于语音量，不含静音）
              COALESCE(SUM(CASE WHEN COALESCE(no_transcribe,0)=0 AND (transcript_status IS NULL OR transcript_status IN ('none','idle','error')) THEN COALESCE(speech_sec, duration_sec) END), 0)
            FROM recordings
            """;
        using var r = cmd.ExecuteReader();
        r.Read();
        return (
            (int)(r.IsDBNull(0) ? 0 : r.GetInt64(0)),
            (int)(r.IsDBNull(1) ? 0 : r.GetInt64(1)),
            (int)(r.IsDBNull(2) ? 0 : r.GetInt64(2)),
            (int)(r.IsDBNull(3) ? 0 : r.GetInt64(3)),
            (int)(r.IsDBNull(4) ? 0 : r.GetInt64(4)),
            r.IsDBNull(5) ? 0 : r.GetDouble(5));
    }

    /// <summary>
    /// 批量回填/校正录音总时长（秒）：填空缺值，或与已有值偏差超过 10 秒时
    /// 以探测到的容器真实时长为准更新（旧版时长取末段 end，尾部静音长会偏短）。
    /// </summary>
    public int SetDurations(List<(long Id, double DurationSec)> list)
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        var n = 0;
        using var tx = c.BeginTransaction();
        cmd.Transaction = tx;
        cmd.CommandText = """
            UPDATE recordings SET duration_sec=@d
            WHERE id=@id AND (duration_sec IS NULL OR duration_sec <= 0 OR ABS(duration_sec - @d) > 10)
            """;
        var pD = cmd.Parameters.Add("@d", SqliteType.Real);
        var pId = cmd.Parameters.Add("@id", SqliteType.Integer);
        foreach (var (id, dur) in list)
        {
            pD.Value = dur;
            pId.Value = id;
            n += cmd.ExecuteNonQuery();
        }
        tx.Commit();
        return n;
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
        // 终态/起点统一清空流式字段：done 置进度 100，其余归零；partial 清空
        cmd.CommandText = """
            UPDATE recordings
            SET transcript_status=@ts, transcript_path=@tp, language=@lang,
                transcript_attempts = MAX(0, transcript_attempts + @delta),
                transcript_progress = @prog,
                transcript_partial = NULL
            WHERE id=@id
            """;
        cmd.Parameters.AddWithValue("@ts", status);
        cmd.Parameters.AddWithValue("@tp", path);
        cmd.Parameters.AddWithValue("@lang", (object?)lang ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@delta", attemptsDelta);
        cmd.Parameters.AddWithValue("@prog", status == "done" ? 100.0 : 0.0);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>转写进行中：更新实时进度（0-100）与已转出的部分文本（流式预览）。</summary>
    public void UpdateTranscriptProgress(long id, double progress, string partial)
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE recordings
            SET transcript_status='pending', transcript_progress=@p, transcript_partial=@t
            WHERE id=@id AND transcript_status='pending'
            """;
        cmd.Parameters.AddWithValue("@p", Math.Clamp(progress, 0, 99.9));
        cmd.Parameters.AddWithValue("@t", partial);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>转写成功后落库全文（供检索）与时长（秒）。</summary>
    /// <summary>
    /// 转写完成落库：全文与双时长（总时长=容器实际时长；有声音时长=VAD 后语音时长）。
    /// speechSec 传 null 时保留原值（旧版边车无此字段）。
    /// </summary>
    public void SaveTranscriptResult(long id, string plainText, double durationSec, double? speechSec)
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE recordings SET
                transcript_text = @t,
                duration_sec    = @d,
                speech_sec      = COALESCE(@s, speech_sec)
            WHERE id = @id
            """;
        cmd.Parameters.AddWithValue("@t", plainText);
        cmd.Parameters.AddWithValue("@d", durationSec);
        cmd.Parameters.AddWithValue("@s", (object?)speechSec ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 更新标题/备注/分类：传 null 表示不修改；contact 传空字符串表示清空分类（置 NULL，kind 同步清除）。
    /// </summary>
    public void UpdateMeta(long id, string? title, string? note, string? contact = null, string? contactKind = null)
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            UPDATE recordings
            SET title = COALESCE(@title, title),
                note = COALESCE(@note, note),
                contact = CASE WHEN @contact IS NULL THEN contact
                               WHEN @contact = '' THEN NULL ELSE @contact END,
                contact_kind = CASE WHEN @contact IS NULL THEN contact_kind
                                    WHEN @contact = '' THEN NULL
                                    ELSE COALESCE(@ckind, contact_kind) END
            WHERE id=@id
            """;
        cmd.Parameters.AddWithValue("@title", (object?)title ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@note", (object?)note ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@contact", (object?)contact ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@ckind", (object?)contactKind ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@id", id);
        cmd.ExecuteNonQuery();
    }

    /// <summary>
    /// 分类聚合：联系人/电话号码 → 条数、总字节、最近录音时间。
    /// 供前端「来源分类」下拉与各分类计数徽标。
    /// </summary>
    public List<ContactGroup> GetContacts()
    {
        var list = new List<ContactGroup>();
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT contact, COALESCE(MAX(contact_kind), 'unknown') AS kind,
                   COUNT(*) AS cnt, COALESCE(SUM(size), 0) AS bytes,
                   COALESCE(MAX(recorded_at), 0) AS lastAt
            FROM recordings
            WHERE contact IS NOT NULL AND contact != ''
            GROUP BY contact ORDER BY lastAt DESC
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
        {
            list.Add(new ContactGroup(
                r.GetString(0), r.GetString(1), r.GetInt64(2), r.GetInt64(3), r.GetInt64(4)));
        }
        return list;
    }

    /// <summary>月份聚合（按录音时间）：2026-11 → 条数。供前端「按月筛选」。</summary>
    public List<MonthGroup> GetMonths()
    {
        var list = new List<MonthGroup>();
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT strftime('%Y-%m', recorded_at, 'unixepoch') AS m, COUNT(*) AS cnt
            FROM recordings WHERE recorded_at > 0
            GROUP BY m ORDER BY m DESC
            """;
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add(new MonthGroup(r.GetString(0), r.GetInt64(1)));
        return list;
    }

    /// <summary>存量数据分类回填：找出 contact 为空的 id 与 rel_path，由调用方提取后逐条写入。</summary>
    public List<(long Id, string RelPath)> GetContactBackfillCandidates()
    {
        var list = new List<(long, string)>();
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT id, rel_path FROM recordings WHERE contact IS NULL OR contact = ''";
        using var r = cmd.ExecuteReader();
        while (r.Read())
            list.Add((r.GetInt64(0), r.GetString(1)));
        return list;
    }

    /// <summary>写回单条分类（回填用）。</summary>
    public void SetContact(long id, string? contact, string? kind)
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = "UPDATE recordings SET contact=@ct, contact_kind=@k WHERE id=@id";
        cmd.Parameters.AddWithValue("@ct", (object?)contact ?? DBNull.Value);
        cmd.Parameters.AddWithValue("@k", (object?)kind ?? DBNull.Value);
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

    /// <summary>
    /// 全库聚合统计（前端顶部统计条）：总数、总字节、总时长（秒）、已转写条数。
    /// 单次全表聚合查询，量级（万条内）开销可忽略。
    /// </summary>
    public (long Count, long Bytes, double DurationSec, long Transcribed) GetStats()
    {
        using var c = OpenConn();
        using var cmd = c.CreateCommand();
        cmd.CommandText = """
            SELECT COUNT(*),
                   COALESCE(SUM(size), 0),
                   COALESCE(SUM(duration_sec), 0),
                   SUM(CASE WHEN transcript_status = 'done' THEN 1 ELSE 0 END)
            FROM recordings
            """;
        using var r = cmd.ExecuteReader();
        r.Read();
        return (r.GetInt64(0), r.GetInt64(1), r.IsDBNull(2) ? 0 : r.GetDouble(2), r.IsDBNull(3) ? 0 : r.GetInt64(3));
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

    private static string BuildWhere(long from, long to, string? q, string? contact, SqliteCommand cmd)
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
        if (!string.IsNullOrWhiteSpace(contact))
        {
            clauses.Add("contact = @contact");
            cmd.Parameters.AddWithValue("@contact", contact);
        }
        if (!string.IsNullOrWhiteSpace(q))
        {
            // 关键词同时命中 文件名/标题/路径/备注/文字稿全文（模糊）
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
            SpeechSec = r.IsDBNull(r.GetOrdinal("speech_sec")) ? null : r.GetDouble(r.GetOrdinal("speech_sec")),
            TranscriptAttempts = r.GetInt32(r.GetOrdinal("transcript_attempts")),
            TranscriptProgress = r.IsDBNull(r.GetOrdinal("transcript_progress")) ? 0 : r.GetDouble(r.GetOrdinal("transcript_progress")),
            TranscriptPartial = r.IsDBNull(r.GetOrdinal("transcript_partial")) ? null : r.GetString(r.GetOrdinal("transcript_partial")),
            Note = r.IsDBNull(r.GetOrdinal("note")) ? null : r.GetString(r.GetOrdinal("note")),
            Contact = r.IsDBNull(r.GetOrdinal("contact")) ? null : r.GetString(r.GetOrdinal("contact")),
            ContactKind = r.IsDBNull(r.GetOrdinal("contact_kind")) ? null : r.GetString(r.GetOrdinal("contact_kind")),
            NoTranscribe = !r.IsDBNull(r.GetOrdinal("no_transcribe")) && r.GetInt64(r.GetOrdinal("no_transcribe")) != 0,
            TranscribeMs = r.IsDBNull(r.GetOrdinal("transcribe_ms")) ? null : r.GetInt64(r.GetOrdinal("transcribe_ms")),
            TranscribeStartedAt = r.IsDBNull(r.GetOrdinal("transcribe_started_at")) ? null : r.GetInt64(r.GetOrdinal("transcribe_started_at")),
            CreatedAt = DateTime.TryParse(r.GetString(r.GetOrdinal("created_at")), out var d) ? d : DateTime.MinValue
        };
    }
}

/// <summary>分类聚合行（联系人/电话号码分组）。</summary>
public record ContactGroup(string Contact, string Kind, long Count, long TotalBytes, long LastRecordedAt);

/// <summary>月份聚合行。</summary>
public record MonthGroup(string Month, long Count);
