package com.voicvault.app

import android.app.NotificationChannel
import android.app.NotificationManager
import android.content.Context
import android.net.Uri
import android.os.Build
import androidx.core.app.NotificationCompat
import androidx.work.CoroutineWorker
import androidx.work.WorkerParameters
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/**
 * 后台同步任务：扫描本地录音 -> 增量求差 -> 上传缺失项。
 * 由 WorkManager 周期性或一次性触发。
 */
class SyncWorker(ctx: Context, params: WorkerParameters) : CoroutineWorker(ctx, params) {

    override suspend fun doWork(): Result = withContext(Dispatchers.IO) {
        val prefs = applicationContext.getSharedPreferences("voicvault", Context.MODE_PRIVATE)
        val server = prefs.getString("server", "") ?: ""
        val token = prefs.getString("token", "") ?: ""
        val device = prefs.getString("device", "android") ?: "android"
        val treeStr = prefs.getString("treeUri", "") ?: ""

        if (server.isEmpty() || token.isEmpty() || treeStr.isEmpty()) {
            return@withContext Result.failure()
        }

        val treeUri = Uri.parse(treeStr)
        try {
            val scanned = RecordingScanner.scan(applicationContext, treeUri)
            val entries = scanned.map {
                FileEntry(it.relPath, it.size, it.mtime, it.sha256)
            }
            val diff = ApiClient.diff(server, token, device, entries)

            var ok = 0
            var fail = 0
            for (s in scanned) {
                if (s.relPath !in diff.toUpload) continue
                try {
                    applicationContext.contentResolver.openInputStream(s.doc.uri)?.use { ins ->
                        ApiClient.upload(
                            server, token, device,
                            FileEntry(s.relPath, s.size, s.mtime, s.sha256), ins
                        )
                    }
                    ok++
                    notify("已备份 $ok 个，失败 $fail 个", false)
                } catch (e: Exception) {
                    fail++
                }
            }
            notify("同步完成：成功 $ok，失败 $fail", true)
            Result.success()
        } catch (e: Exception) {
            notify("同步失败：${e.message}", true)
            Result.retry()
        }
    }

    private fun notify(text: String, done: Boolean) {
        val nm = applicationContext.getSystemService(Context.NOTIFICATION_SERVICE) as NotificationManager
        val ch = "voicvault_sync"
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.O) {
            nm.createNotificationChannel(
                NotificationChannel(ch, "录音同步", NotificationManager.IMPORTANCE_LOW)
            )
        }
        nm.notify(
            1,
            NotificationCompat.Builder(applicationContext, ch)
                .setContentTitle("录音保险箱")
                .setContentText(text)
                .setSmallIcon(android.R.drawable.ic_menu_upload)
                .setOnlyAlertOnce(true)
                .build()
        )
    }
}
