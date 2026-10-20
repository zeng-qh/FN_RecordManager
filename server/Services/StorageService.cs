using System.Security.Cryptography;

namespace VoiceVault.Server.Services;

/// <summary>
/// 负责把上传的音频流落盘到 <c>recordings/YYYY/MM/DD/</c> 时间线目录，
/// 并在写入过程中计算 SHA-256 校验，确保与客户端上报一致。
/// </summary>
public class StorageService
{
    private readonly string _dataDir;
    private readonly string _recordingsDir;
    private readonly string _tmpDir;

    public StorageService(string dataDir)
    {
        _dataDir = dataDir;
        _recordingsDir = Path.Combine(dataDir, "recordings");
        _tmpDir = Path.Combine(dataDir, "tmp");
        Directory.CreateDirectory(_recordingsDir);
        Directory.CreateDirectory(_tmpDir);
    }

    public string RecordingsDir => _recordingsDir;

    private static string Sanitize(string name)
    {
        foreach (var c in Path.GetInvalidFileNameChars())
            name = name.Replace(c, '_');
        if (string.IsNullOrWhiteSpace(name))
            name = "recording";
        return name;
    }

    /// <summary>
    /// 写入临时文件并流式计算 SHA-256，校验通过后移入时间线目录。
    /// </summary>
    /// <returns>落盘路径、录音时间(Unix秒)、实际SHA、字节数。</returns>
    public async Task<(string storedPath, long recordedAt, string sha, long size)> SaveAsync(
        Stream stream, string relPath, string expectedSha, long mtime)
    {
        var fileName = Sanitize(Path.GetFileName(relPath));
        if (!fileName.Contains('.'))
            fileName += ".m4a";

        var tmpPath = Path.Combine(_tmpDir, Guid.NewGuid().ToString("N") + ".tmp");

        await using var fs = new FileStream(tmpPath, FileMode.Create, FileAccess.Write);
        using var shaAlg = SHA256.Create();
        var buffer = new byte[81920];
        int read;
        while ((read = await stream.ReadAsync(buffer)) > 0)
        {
            await fs.WriteAsync(buffer.AsMemory(0, read));
            shaAlg.TransformBlock(buffer, 0, read, null, 0);
        }
        shaAlg.TransformFinalBlock(buffer, 0, 0);
        await fs.FlushAsync();
        var size = fs.Length;
        var actualSha = Convert.ToHexString(shaAlg.Hash!).ToLowerInvariant();

        // 客户端未上报校验值时（如网页端浏览器上传）跳过比对，
        // 但服务端仍计算真实 SHA 存库，(device, sha256) 去重不受影响。
        var skipVerify = string.IsNullOrWhiteSpace(expectedSha);
        if (!skipVerify && !string.Equals(actualSha, expectedSha, StringComparison.OrdinalIgnoreCase))
        {
            File.Delete(tmpPath);
            throw new InvalidDataException(
                $"SHA-256 mismatch: client={expectedSha} server={actualSha}");
        }

        var recordedAt = mtime > 0
            ? mtime
            : DateTimeOffset.UtcNow.ToUnixTimeSeconds();

        var dto = DateTimeOffset.FromUnixTimeSeconds(recordedAt);
        var dir = Path.Combine(_recordingsDir,
            dto.Year.ToString("0000"), dto.Month.ToString("00"), dto.Day.ToString("00"));
        Directory.CreateDirectory(dir);

        var target = Path.Combine(dir, fileName);
        if (File.Exists(target))
        {
            var baseName = Path.GetFileNameWithoutExtension(fileName);
            var ext = Path.GetExtension(fileName);
            target = Path.Combine(dir, $"{baseName}_{actualSha[..8]}{ext}");
        }

        File.Move(tmpPath, target);
        return (target, recordedAt, actualSha, size);
    }
}
