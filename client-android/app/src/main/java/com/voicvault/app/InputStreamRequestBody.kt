package com.voicvault.app

import okhttp3.MediaType
import okhttp3.RequestBody
import okio.BufferedSink
import okio.source
import java.io.InputStream

/**
 * 把 InputStream 适配为 OkHttp 的请求体，避免先把大录音读进内存。
 */
class InputStreamRequestBody(
    private val contentType: MediaType?,
    private val stream: InputStream
) : RequestBody() {

    override fun contentType(): MediaType? = contentType

    override fun contentLength(): Long = -1

    override fun writeTo(sink: BufferedSink) {
        stream.use { s -> sink.writeAll(s.source()) }
    }
}
