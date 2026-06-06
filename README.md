# VLA Bridge

VLA (VRC-Library Assist) Unity Bridge の VPM (VRChat Package Manager) リポジトリです。  
VRChat Creator Companion (VCC) からインストールできます。

---

## VCCへのリポジトリ追加方法

1. VCC (VRChat Creator Companion) を起動します
2. **Settings → Packages → Add Repository** を開きます
3. 以下のURLを入力して追加します:

```
https://nyaohtech.github.io/VLABridge/index.json
```

4. 追加後、各プロジェクトの **Manage Packages** から `VLA Bridge` をインストールできます

---

## 含まれるもの

- **VLABridgeWindow.cs** — Unity Editor ウィンドウ  
  VLA アプリ (ws://localhost:27420) と WebSocket 通信し、アセットライブラリの閲覧・インポート・テンプレート管理を行います。

## Unityメニュー

| メニュー | 機能 |
|---|---|
| `VLA > Library Bridge` (Ctrl+Shift+V) | メインウィンドウを開く |
| `VLA > Template Creator` | テンプレート作成ウィンドウ |
| `VLA > Import Template` | テンプレートインポートウィンドウ |

---

## 動作環境

- Unity 2022.3 以降
- VRChat Creator Companion (VCC)
- VLA (VRC-Library Assist) アプリ起動済み

## ライセンス

MIT License
