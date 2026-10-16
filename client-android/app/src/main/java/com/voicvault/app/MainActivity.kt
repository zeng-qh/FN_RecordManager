package com.voicvault.app

import android.content.Intent
import android.content.SharedPreferences
import android.net.Uri
import android.os.Bundle
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.work.*
import com.voicvault.app.databinding.ActivityMainBinding
import java.util.concurrent.TimeUnit

class MainActivity : AppCompatActivity() {

    private lateinit var binding: ActivityMainBinding
    private lateinit var prefs: SharedPreferences
    private var treeUri: Uri? = null

    private val pickDir = registerForActivityResult(
        ActivityResultContracts.OpenDocumentTree()
    ) { uri ->
        uri?.let {
            contentResolver.takePersistableUriPermission(
                it, Intent.FLAG_GRANT_READ_URI_PERMISSION
            )
            treeUri = it
            prefs.edit().putString("treeUri", it.toString()).apply()
            binding.dirText.text = "目录已选择：${it.lastPathSegment}"
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        binding = ActivityMainBinding.inflate(layoutInflater)
        setContentView(binding.root)

        prefs = getSharedPreferences("voicvault", MODE_PRIVATE)
        binding.serverUrl.setText(prefs.getString("server", "http://192.168.1.10:8080"))
        binding.token.setText(prefs.getString("token", ""))

        val savedTree = prefs.getString("treeUri", "")
        if (!savedTree.isNullOrEmpty()) {
            treeUri = Uri.parse(savedTree)
            binding.dirText.text = "目录已选择（已保存）"
        }

        binding.pickDir.setOnClickListener { pickDir.launch(null) }
        binding.saveStart.setOnClickListener { saveAndStart() }
        binding.syncNow.setOnClickListener { syncNow() }

        binding.log.text =
            "配置完成后点「保存并启动自动同步」。\n小米录音目录通常为 MIUI/sound_recorder/"
    }

    private fun saveAndStart() {
        val server = binding.serverUrl.text.toString().trim()
        val token = binding.token.text.toString().trim()
        if (server.isEmpty() || token.isEmpty() || treeUri == null) {
            binding.log.text = "请先填写服务器地址、令牌，并选择录音目录。"
            return
        }
        prefs.edit().apply {
            putString("server", server)
            putString("token", token)
            putString("device", "android")
            apply()
        }

        val constraints = Constraints.Builder()
            .setRequiredNetworkType(NetworkType.CONNECTED)
            .build()
        val req = PeriodicWorkRequestBuilder<SyncWorker>(15, TimeUnit.MINUTES)
            .setConstraints(constraints)
            .build()
        WorkManager.getInstance(this).enqueueUniquePeriodicWork(
            "voicvault-sync", ExistingPeriodicWorkPolicy.UPDATE, req
        )
        binding.log.text = "已启动自动同步（每 15 分钟，需联网）。"
    }

    private fun syncNow() {
        val req = OneTimeWorkRequestBuilder<SyncWorker>().build()
        WorkManager.getInstance(this).enqueueUniqueWork(
            "voicvault-sync-now", ExistingWorkPolicy.REPLACE, req
        )
        binding.log.text = "已提交一次手动同步。"
    }
}
