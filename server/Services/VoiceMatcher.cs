namespace VoiceVault.Server.Services;

/// <summary>
/// 声纹注册匹配：把转写簇代表声纹与已注册声纹逐一算余弦相似度，
/// 命中注册名则用名字替换「说话人N」显示。
/// 采用「单条记录内一对一分配」：同一录音的多个簇共享信道，彼此相似度
/// 天然偏高（实测真不同人簇间也能到 0.68–0.86），若允许逐簇独立匹配，
/// 某档案被删除后其"邻居簇"会被同录音的其他档案错误认领——因此每个
/// 档案在同一条记录内至多被一个簇认领，全局按相似度降序贪心分配。
/// </summary>
public static class VoiceMatcher
{
    /// <summary>
    /// 为一条记录的全部说话人簇批量认名（就地写 Name）。
    /// speakers 与 index 任意一方为空直接跳过。
    /// </summary>
    public static void AssignNames(
        IReadOnlyList<AsrSpeakerInfo> speakers,
        IReadOnlyList<(long ProfileId, string Name, float[] Embedding)> index,
        double threshold,
        double margin)
    {
        if (speakers.Count == 0 || index.Count == 0)
            return;
        var usable = speakers.Where(s => s.Embedding is { Count: > 0 }).ToList();
        if (usable.Count == 0)
            return;

        // 每簇 × 每档案：取该档案全部注册样本的最高相似度（一人多样本加固）
        var profiles = index.GroupBy(x => x.ProfileId)
            .Select(g => (ProfileId: g.Key, Name: g.First().Name,
                          Embeddings: g.Select(x => x.Embedding).ToList()))
            .ToList();
        var score = new double[usable.Count, profiles.Count];
        for (var i = 0; i < usable.Count; i++)
            for (var j = 0; j < profiles.Count; j++)
                score[i, j] = profiles[j].Embeddings.Max(e => Cosine(usable[i].Embedding!, e));

        // 全局降序贪心一对一：档案被占用后，同录音的"邻居簇"无法再认领它
        var pairs = new List<(int I, int J, double S)>();
        for (var i = 0; i < usable.Count; i++)
            for (var j = 0; j < profiles.Count; j++)
                if (score[i, j] >= threshold)
                    pairs.Add((i, j, score[i, j]));
        pairs.Sort((a, b) => b.S.CompareTo(a.S));

        var assigned = new bool[usable.Count];
        var usedProfiles = new HashSet<long>();
        foreach (var (i, j, s) in pairs)
        {
            if (assigned[i] || !usedProfiles.Add(profiles[j].ProfileId))
                continue; // 簇已有名 / 档案已被本记录其他簇认领
            // 歧义判据：该簇对其他「尚未被占用」档案的最好分数与本次差距不足则放弃
            var bestOther = 0.0;
            for (var k = 0; k < profiles.Count; k++)
                if (k != j && !usedProfiles.Contains(profiles[k].ProfileId))
                    bestOther = Math.Max(bestOther, score[i, k]);
            if (bestOther > s - margin)
                continue;
            usable[i].Name = profiles[j].Name;
            assigned[i] = true;
        }
    }

    /// <summary>余弦相似度（已归一化向量等价于点积；此处不假设输入已归一化）。</summary>
    public static double Cosine(IReadOnlyList<float> a, IReadOnlyList<float> b)
    {
        if (a.Count != b.Count || a.Count == 0) return 0;
        double dot = 0, na = 0, nb = 0;
        for (var i = 0; i < a.Count; i++)
        {
            dot += (double)a[i] * b[i];
            na += (double)a[i] * a[i];
            nb += (double)b[i] * b[i];
        }
        if (na <= 0 || nb <= 0) return 0;
        return dot / (Math.Sqrt(na) * Math.Sqrt(nb));
    }
}
