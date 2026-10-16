# VoiceVault 安卓客户端

把小米（及多数国产安卓）系统录音机的录音，增量备份到你的 VoiceVault 服务端（NAS）。

## 功能
- 通过系统「文档选择器（SAF）」授权录音目录（小米默认 `MIUI/sound_recorder/`）。
- WorkManager 每 15 分钟后台自动增量同步（需联网）。
- 按 SHA-256 去重，重复运行只补传新文件，不占流量。
- 同步进度以通知形式展示。

## 构建
> 本地没有 gradle wrapper jar，可用 Android Studio 打开，或在仓库根执行一次 `gradle wrapper` 后再用 `./gradlew`。
> 也可直接由 GitHub Actions 出 APK（见 `.github/workflows/android.yml`，手动触发即可在 Artifacts 下载）。

```bash
cd client-android
gradle wrapper            # 若尚未生成 wrapper
./gradlew assembleRelease # 产物在 app/build/outputs/apk/release/
```

## 使用
1. 在服务端（网页端设置）拿到服务器地址与访问令牌。
2. 打开 App，填写地址、令牌，点「选择录音目录」并选中 `MIUI/sound_recorder`。
3. 点「保存并启动自动同步」。之后每次联网会自动备份；也可点「立即同步一次」手动触发。

## 隐私
录音仅上传到你自己的服务端，不经过任何第三方。
