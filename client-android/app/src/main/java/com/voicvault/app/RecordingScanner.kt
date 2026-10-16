package com.voicvault.app

import android.content.Context
import android.net.Uri
import androidx.documentfile.provider.DocumentFile
import java.io.InputStream
import java.security.MessageDigest

/**
 * 通过 SAF（DocumentsContract）遍历用户授权的录音目录树，
 * 收集音频文件并计算 SHA-256（用于服务端增量去重）。
 */
object RecordingScanner {

    private val AUDIO_EXT =
        setOf("m4a", "mp3", "wav", "flac", "ogg", "aac", "amr")

    data class Scanned(
        val relPath: String,
        val doc: DocumentFile,
        val size: Long,
        val mtime: Long,
        val sha256: String
    )

    fun scan(context: Context, treeUri: Uri): List<Scanned> {
        val root = DocumentFile.fromTreeUri(context, treeUri) ?: return emptyList()
        val out = mutableListOf<Scanned>()
        walk(context, root, "", out)
        return out
    }

    private fun walk(context: Context, dir: DocumentFile, prefix: String, out: MutableList<Scanned>) {
        for (f in dir.listFiles()) {
            val name = f.name ?: continue
            val rel = if (prefix.isEmpty()) name else "$prefix/$name"
            when {
                f.isDirectory -> walk(context, f, rel, out)
                AUDIO_EXT.contains(name.substringAfterLast('.', "").lowercase()) -> {
                    val size = f.length()
                    val mtime = f.lastModified() / 1000
                    val sha = sha256(context, f.uri)
                    out.add(Scanned(rel, f, size, mtime, sha))
                }
            }
        }
    }

    private fun sha256(context: Context, uri: Uri): String {
        val md = MessageDigest.getInstance("SHA-256")
        var ins: InputStream? = null
        try {
            ins = context.contentResolver.openInputStream(uri)
            val buf = ByteArray(8192)
            var n: Int
            while (ins != null) {
                n = ins.read(buf)
                if (n <= 0) break
                md.update(buf, 0, n)
            }
        } finally {
            ins?.close()
        }
        return md.digest().joinToString("") { "%02x".format(it) }
    }
}
