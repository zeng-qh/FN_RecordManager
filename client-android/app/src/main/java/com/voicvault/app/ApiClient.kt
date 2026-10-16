package com.voicvault.app

import android.util.Log
import okhttp3.HttpUrl.Companion.toHttpUrlOrNull
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.MultipartBody
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody.Companion.toRequestBody
import org.json.JSONArray
import org.json.JSONObject
import java.io.IOException
import java.io.InputStream
import java.util.concurrent.TimeUnit

data class FileEntry(
    val relPath: String,
    val size: Long,
    val mtime: Long,
    val sha256: String
)

data class DiffResponse(
    val toUpload: List<String>,
    val serverHas: List<String>
)

/**
 * 与服务端 REST API 交互：增量求差 + 分块上传。
 */
object ApiClient {

    private val JSON = "application/json; charset=utf-8".toMediaType()
    private val OCTET = "application/octet-stream".toMediaType()

    private val client = OkHttpClient.Builder()
        .connectTimeout(30, TimeUnit.SECONDS)
        .writeTimeout(600, TimeUnit.SECONDS)
        .readTimeout(60, TimeUnit.SECONDS)
        .build()

    fun diff(server: String, token: String, device: String, files: List<FileEntry>): DiffResponse {
        val arr = JSONArray()
        for (f in files) {
            val o = JSONObject()
            o.put("relPath", f.relPath)
            o.put("size", f.size)
            o.put("mtime", f.mtime)
            o.put("sha256", f.sha256)
            arr.put(o)
        }
        val body = JSONObject().apply {
            put("device", device)
            put("files", arr)
        }
        val req = Request.Builder()
            .url(server.trimEnd('/') + "/api/sync/diff")
            .addHeader("Authorization", "Bearer $token")
            .post(body.toString().toRequestBody(JSON))
            .build()

        client.newCall(req).execute().use { resp ->
            if (!resp.isSuccessful) throw IOException("diff failed: ${resp.code}")
            val json = JSONObject(resp.body!!.string())
            val toUp = mutableListOf<String>()
            val srv = mutableListOf<String>()
            json.optJSONArray("toUpload")?.let { for (i in 0 until it.length()) toUp.add(it.getString(i)) }
            json.optJSONArray("serverHas")?.let { for (i in 0 until it.length()) srv.add(it.getString(i)) }
            return DiffResponse(toUp, srv)
        }
    }

    fun upload(
        server: String,
        token: String,
        device: String,
        entry: FileEntry,
        stream: InputStream
    ) {
        val form = MultipartBody.Builder().setType(MultipartBody.FORM)
            .addFormDataPart("relPath", entry.relPath)
            .addFormDataPart("device", device)
            .addFormDataPart("size", entry.size.toString())
            .addFormDataPart("mtime", entry.mtime.toString())
            .addFormDataPart("sha256", entry.sha256)
            .addFormDataPart(
                "file",
                entry.relPath.substringAfterLast('/'),
                InputStreamRequestBody(OCTET, stream)
            )
            .build()

        val req = Request.Builder()
            .url(server.trimEnd('/') + "/api/upload")
            .addHeader("Authorization", "Bearer $token")
            .post(form)
            .build()

        client.newCall(req).execute().use { resp ->
            if (!resp.isSuccessful) {
                throw IOException("upload failed: ${resp.code} ${resp.body?.string()}")
            }
        }
    }
}
