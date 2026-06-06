// VLA Unity Bridge — Editor Window
using UnityEngine;
using UnityEditor;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Net.WebSockets;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace VLABridge
{
    public class VLABridgeWindow : EditorWindow
    {
        private const string PREFS_WS_URL = "VLABridge_WsUrl";
        private const float RECONNECT_INTERVAL = 5f;
        private const float HEARTBEAT_INTERVAL = 10f;
        private const int CARD_WIDTH = 160;
        private const int CARD_HEIGHT = 240;
        private const int THUMB_SIZE = 140;

        internal enum ConnectionState { Disconnected, Connecting, Connected }
        internal ConnectionState _connState = ConnectionState.Disconnected;
        private ClientWebSocket _ws;
        private CancellationTokenSource _cts;
        private string _wsUrl = "ws://localhost:27420/vla/";
        private float _lastReconnectAttempt;
        private float _lastHeartbeat;
        private string _statusMessage = "未接続";
        private bool _hasConnectedOnce = false;

        private List<VLALibraryItem> _libraryItems = new List<VLALibraryItem>();
        private Dictionary<string, Texture2D> _thumbnailCache = new Dictionary<string, Texture2D>();
        private HashSet<string> _thumbLoadingIds = new HashSet<string>();
        private string _searchFilter = "";
        private Vector2 _scrollPosition;

        private List<VLAFavoriteList> _favorites = new List<VLAFavoriteList>();
        private int _selectedFavoriteIndex = 0;

        // インポート/解凍 待機
        private bool _isWaitingImport = false;
        private string _waitingImportItemName = "";
        private bool _isWaitingUnarchive = false;
        private string _waitingUnarchiveItemId = "";
        private string _waitingUnarchiveItemName = "";
        private string _unarchiveProgressStatus = "";
        private string _unarchiveProgressMessage = "";

        private VLAImportedData _importedData;
        private bool _hideImported = false;
        private Dictionary<string, int> _selectedPackageIndex = new Dictionary<string, int>();

        private readonly Queue<string> _receivedMessages = new Queue<string>();
        private readonly object _msgLock = new object();

        // ── テンプレート ──
        private bool _isExportingTemplate = false;
        private string _exportProgressMessage = "";
        private string _exportTemplateName = "";
        private string _vlaSavePath = "";

        // ── テンプレートリストキャッシュ（VLATemplateImportWindow と共有） ──
        internal List<VLATemplateItem> _cachedTemplateList  = new List<VLATemplateItem>();
        internal bool                  _templateListReceived = false;

        [MenuItem("VLA/Library Bridge %#v")]
        public static void ShowWindow()
        {
            var win = GetWindow<VLABridgeWindow>("VLA Library");
            win.minSize = new Vector2(400, 300);
            win.Show();
        }

        private void OnEnable()
        {
            _wsUrl = EditorPrefs.GetString(PREFS_WS_URL, "ws://localhost:27420/vla/");
            LoadImportedData();
            _lastReconnectAttempt = Time.realtimeSinceStartup; // 初回はOnEnableのConnectAsyncに任せる
            ConnectAsync();
            EditorApplication.update += OnEditorUpdate;
        }

        private void OnDisable() { EditorApplication.update -= OnEditorUpdate; Disconnect(); }
        private void OnDestroy() => Disconnect();

        private void OnEditorUpdate()
        {
            lock (_msgLock) { while (_receivedMessages.Count > 0) ProcessMessage(_receivedMessages.Dequeue()); }
            if (_connState == ConnectionState.Disconnected && Time.realtimeSinceStartup - _lastReconnectAttempt > RECONNECT_INTERVAL)
            { _lastReconnectAttempt = Time.realtimeSinceStartup; ConnectAsync(); }
            if (_connState == ConnectionState.Connected && Time.realtimeSinceStartup - _lastHeartbeat > HEARTBEAT_INTERVAL)
            { _lastHeartbeat = Time.realtimeSinceStartup; SendAsync("{\"type\":\"ping\"}"); }
        }

        // ═══════════════════════════════════════════
        // WebSocket
        // ═══════════════════════════════════════════
        private async void ConnectAsync()
        {
            if (_connState != ConnectionState.Disconnected) return;
            _connState = ConnectionState.Connecting; _statusMessage = "接続中…";
            try
            {
                _cts = new CancellationTokenSource();
                _ws = new ClientWebSocket();
                _ws.Options.KeepAliveInterval = TimeSpan.FromSeconds(15);
                await _ws.ConnectAsync(new Uri(_wsUrl), _cts.Token);
                _connState = ConnectionState.Connected; _hasConnectedOnce = true;
                _statusMessage = "VLA 接続済み"; _lastHeartbeat = Time.realtimeSinceStartup;
                SendAsync(JsonUtility.ToJson(new WsMessage { type = "unity_connected", project_name = Application.productName, project_path = GetProjectPath() }));
                SendAsync("{\"type\":\"get_unity_library\"}");
                SendAsync("{\"type\":\"get_favorites\"}");
                SendAsync("{\"type\":\"get_save_path\"}");
                SendAsync("{\"type\":\"get_template_list\"}");
                _ = ReceiveLoopAsync(); Repaint();
            }
            catch
            {
                _connState = ConnectionState.Disconnected;
                _statusMessage = _hasConnectedOnce ? "VLA 切断 — 再接続中…" : "VLA 未起動 — 自動再接続中…";
            }
        }

        private void Disconnect()
        {
            if (_ws != null) { try { if (_ws.State == WebSocketState.Open) SendAsync(JsonUtility.ToJson(new WsMessage { type = "unity_disconnected", project_path = GetProjectPath() })); _cts?.Cancel(); _ws?.Dispose(); } catch { } }
            _ws = null; _connState = ConnectionState.Disconnected; _statusMessage = "切断済み";
        }

        private async Task ReceiveLoopAsync()
        {
            var buf = new byte[1024 * 64]; var sb = new StringBuilder();
            try { while (_ws != null && _ws.State == WebSocketState.Open && !_cts.Token.IsCancellationRequested)
            { var r = await _ws.ReceiveAsync(new ArraySegment<byte>(buf), _cts.Token); if (r.MessageType == WebSocketMessageType.Close) break;
              sb.Append(Encoding.UTF8.GetString(buf, 0, r.Count)); if (r.EndOfMessage) { lock (_msgLock) _receivedMessages.Enqueue(sb.ToString()); sb.Clear(); } } }
            catch (OperationCanceledException) { } catch { }
            _connState = ConnectionState.Disconnected; _statusMessage = "VLA 切断 — 再接続中…";
        }

        private async void SendAsync(string json)
        { if (_ws == null || _ws.State != WebSocketState.Open) return;
          try { await _ws.SendAsync(new ArraySegment<byte>(Encoding.UTF8.GetBytes(json)), WebSocketMessageType.Text, true, _cts.Token); } catch { } }

        // ═══════════════════════════════════════════
        // メッセージ処理
        // ═══════════════════════════════════════════
        private void ProcessMessage(string json)
        {
            try
            {
                if (json.Contains("\"type\":\"pong\"")) return;

                if (json.Contains("\"type\":\"save_path\""))
                {
                    var sp = JsonUtility.FromJson<SavePathResponse>(json);
                    if (sp != null && !string.IsNullOrEmpty(sp.path))
                        _vlaSavePath = sp.path;
                    Repaint();
                    return;
                }

                if (json.Contains("\"type\":\"unity_library\""))
                {
                    var r = JsonUtility.FromJson<UnityLibraryResponse>(json);
                    if (r?.items != null)
                    {
                        _libraryItems = r.items;
                        _statusMessage = $"VLA 接続済み — {_libraryItems.Count} アイテム";
                        foreach (var item in _libraryItems)
                            if (!_thumbnailCache.ContainsKey(item.item_id) && !_thumbLoadingIds.Contains(item.item_id))
                                RequestThumbnail(item.item_id);
                    }
                    Repaint();
                }
                else if (json.Contains("\"type\":\"unity_thumbnail\""))
                {
                    var t = JsonUtility.FromJson<ThumbnailResponse>(json);
                    if (t != null && !string.IsNullOrEmpty(t.item_id) && !string.IsNullOrEmpty(t.thumbnail_base64))
                    { _thumbLoadingIds.Remove(t.item_id); try { var b = Convert.FromBase64String(t.thumbnail_base64); var tex = new Texture2D(2, 2); if (tex.LoadImage(b)) _thumbnailCache[t.item_id] = tex; } catch { } }
                    Repaint();
                }
                else if (json.Contains("\"type\":\"favorites_list\""))
                {
                    var f = JsonUtility.FromJson<FavoritesResponse>(json);
                    if (f?.favorites != null) _favorites = f.favorites;
                    Repaint();
                }
                else if (json.Contains("\"type\":\"unarchive_progress\""))
                {
                    // 解凍進捗をリアルタイム表示
                    var up = JsonUtility.FromJson<UnarchiveProgressMessage>(json);
                    if (up != null && up.item_id == _waitingUnarchiveItemId)
                    {
                        _unarchiveProgressStatus = up.status;
                        _unarchiveProgressMessage = up.message;
                    }
                    Repaint();
                }
                else if (json.Contains("\"type\":\"unarchive_result\""))
                {
                    var ur = JsonUtility.FromJson<UnarchiveResultMessage>(json);
                    if (ur != null && ur.item_id == _waitingUnarchiveItemId)
                    {
                        _isWaitingUnarchive = false;
                        _waitingUnarchiveItemId = "";
                        _waitingUnarchiveItemName = "";
                        _unarchiveProgressStatus = "";
                        _unarchiveProgressMessage = "";
                        if (ur.success)
                        {
                            Debug.Log($"[VLA Bridge] 解凍完了: {ur.item_id}");
                            // 解凍完了 → ライブラリを自動リロード
                            SendAsync("{\"type\":\"get_unity_library\"}");
                        }
                        else
                            Debug.LogWarning($"[VLA Bridge] 解凍失敗: {ur.error}");
                    }
                    Repaint();
                }
                else if (json.Contains("\"type\":\"import_package_result\""))
                {
                    _isWaitingImport = false; _waitingImportItemName = "";
                    var r = JsonUtility.FromJson<ImportResultMessage>(json);
                    if (r != null)
                    {
                        if (r.success) { MarkAsImported(r.item_id, r.package_name); Debug.Log($"[VLA Bridge] インポート成功: {r.package_name}"); }
                        else if (!string.IsNullOrEmpty(r.error) && r.error != "cancelled") Debug.LogWarning($"[VLA Bridge] インポート失敗: {r.error}");
                    }
                    Repaint();
                }
                else if (json.Contains("\"type\":\"package_file_ready\""))
                {
                    _isWaitingImport = false;
                    var f = JsonUtility.FromJson<PackageFileReadyMessage>(json);
                    if (f != null && !string.IsNullOrEmpty(f.file_path))
                    {
                        if (f.item_id != null && f.item_id.StartsWith("template_"))
                        {
                            // テンプレートインポート → VLATemplateImportWindow に転送
                            if (EditorWindow.HasOpenInstances<VLATemplateImportWindow>())
                                EditorWindow.GetWindow<VLATemplateImportWindow>(false, null, false)
                                    ?.OnPackageFileReady(f.item_id, f.package_name, f.file_path);
                        }
                        else
                        {
                            // 通常ライブラリインポート
                            EditorApplication.delayCall += () =>
                                ImportUnityPackageFile(f.file_path, f.item_id, f.package_name);
                        }
                    }
                }
                else if (json.Contains("\"type\":\"thumbnail_updated\""))
                {
                    var t = JsonUtility.FromJson<ThumbnailResponse>(json);
                    if (t != null && !string.IsNullOrEmpty(t.item_id)) { _thumbnailCache.Remove(t.item_id); _thumbLoadingIds.Remove(t.item_id); RequestThumbnail(t.item_id); }
                }
                else if (json.Contains("\"type\":\"template_list\""))
                {
                    // テンプレート一覧を受信 → キャッシュして VLATemplateImportWindow に通知
                    var r = JsonUtility.FromJson<TemplateListResponse>(json);
                    if (r?.templates != null)
                    {
                        _cachedTemplateList   = r.templates;
                        _templateListReceived = true;
                        if (EditorWindow.HasOpenInstances<VLATemplateImportWindow>())
                            EditorWindow.GetWindow<VLATemplateImportWindow>(false, null, false)
                                ?.OnTemplateListReceived(r.templates);
                    }
                    Repaint();
                }
            }
            catch (Exception ex) { Debug.LogWarning($"[VLA Bridge] Parse error: {ex.Message}"); }
        }

        private void RequestThumbnail(string itemId) { _thumbLoadingIds.Add(itemId); SendAsync(JsonUtility.ToJson(new WsMessage { type = "get_unity_thumbnail", item_id = itemId })); }

        // ═══════════════════════════════════════════
        // 解凍リクエスト（ZIP → 展開）
        // ═══════════════════════════════════════════
        private void RequestUnarchive(VLALibraryItem item)
        {
            _isWaitingUnarchive = true;
            _waitingUnarchiveItemId = item.item_id;
            _waitingUnarchiveItemName = item.item_name;
            Repaint();
            SendAsync(JsonUtility.ToJson(new WsMessage { type = "request_unarchive", item_id = item.item_id }));
        }

        // ═══════════════════════════════════════════
        // パッケージインポート
        // ═══════════════════════════════════════════
        private void RequestImportPackage(VLALibraryItem item, string packageName = "")
        {
            _isWaitingImport = true; _waitingImportItemName = item.item_name; Repaint();
            SendAsync(JsonUtility.ToJson(new WsMessage { type = "request_import_package", item_id = item.item_id, project_path = GetProjectPath(), package_name = packageName }));
        }

        private void ImportUnityPackageFile(string filePath, string itemId, string packageName)
        {
            if (!File.Exists(filePath)) { SendImportResult(itemId, packageName, false, "File not found"); return; }
            try
            {
                // 念のため既存のコールバックを先に解除（二重登録防止）
                // 二重登録されると VRChat SDK のビルド・アップロードイベントにも
                // OnImportCompleted が誤発火し、アップロードプロセスが中断される。
                UnregisterImportCallbacks();

                // インポート前のAssets以下のファイル一覧を記録（インポート後の差分検出用）
                _preImportAssets = new HashSet<string>(
                    Directory.GetFiles(Application.dataPath, "*", SearchOption.AllDirectories)
                    .Select(p => p.Replace("\\", "/")), StringComparer.OrdinalIgnoreCase);
                _pendingImportFileSize = new FileInfo(filePath).Length;
                _pendingImportItemId = itemId;
                _pendingImportPackageName = packageName;

                // コールバック登録は ImportPackage の直前に行う
                AssetDatabase.importPackageCompleted += OnImportCompleted;
                AssetDatabase.importPackageCancelled += OnImportCancelled;
                AssetDatabase.importPackageFailed    += OnImportFailed;

                AssetDatabase.ImportPackage(filePath, true);
            }
            catch (Exception ex)
            {
                UnregisterImportCallbacks();
                SendImportResult(itemId, packageName, false, ex.Message);
            }
        }

        private string _pendingImportItemId = "";
        private string _pendingImportPackageName = "";
        private HashSet<string> _preImportAssets;
        private long _pendingImportFileSize;

        private void OnImportCompleted(string packageName)
        {
            UnregisterImportCallbacks();

            // インポートされたファイルを検出して記録
            var importedFiles = DetectImportedFiles();

            SendImportResult(_pendingImportItemId, _pendingImportPackageName, true, "");
            MarkAsImported(_pendingImportItemId, _pendingImportPackageName, importedFiles, _pendingImportFileSize);
        }

        private void OnImportCancelled(string packageName)
        {
            UnregisterImportCallbacks();
            SendImportResult(_pendingImportItemId, _pendingImportPackageName, false, "cancelled");
            _isWaitingImport = false; Repaint();
        }

        private void OnImportFailed(string packageName, string errorMessage)
        {
            UnregisterImportCallbacks();
            SendImportResult(_pendingImportItemId, _pendingImportPackageName, false, errorMessage);
            _isWaitingImport = false; Repaint();
        }

        private void UnregisterImportCallbacks()
        {
            AssetDatabase.importPackageCompleted -= OnImportCompleted;
            AssetDatabase.importPackageCancelled -= OnImportCancelled;
            AssetDatabase.importPackageFailed -= OnImportFailed;
        }

        /// <summary>インポート前後のファイル差分からインポートされたファイルを検出</summary>
        private List<string> DetectImportedFiles()
        {
            var result = new List<string>();
            if (_preImportAssets == null) return result;
            try
            {
                var currentAssets = Directory.GetFiles(Application.dataPath, "*", SearchOption.AllDirectories);
                string dataPath = Application.dataPath.Replace("\\", "/");
                foreach (var f in currentAssets)
                {
                    string normalized = f.Replace("\\", "/");
                    if (!_preImportAssets.Contains(normalized))
                    {
                        // Assets/ からの相対パスで記録
                        string rel = normalized.StartsWith(dataPath)
                            ? "Assets" + normalized.Substring(dataPath.Length)
                            : normalized;
                        result.Add(rel);
                    }
                }
            }
            catch { }
            _preImportAssets = null;
            return result;
        }

        private void SendImportResult(string itemId, string pkgName, bool success, string error)
        { SendAsync(JsonUtility.ToJson(new ImportResultMessage { type = "import_package_result", item_id = itemId, package_name = pkgName, success = success, error = error, project_path = GetProjectPath() })); }

        // ═══════════════════════════════════════════
        // インポート済みデータ管理（ファイル情報付き）
        // ═══════════════════════════════════════════
        private void LoadImportedData() { string p = GetImportedDataPath(); try { _importedData = File.Exists(p) ? JsonUtility.FromJson<VLAImportedData>(File.ReadAllText(p)) ?? new VLAImportedData() : new VLAImportedData(); } catch { _importedData = new VLAImportedData(); } }
        private void SaveImportedData() { try { string p = GetImportedDataPath(); string d = Path.GetDirectoryName(p); if (!Directory.Exists(d)) Directory.CreateDirectory(d); File.WriteAllText(p, JsonUtility.ToJson(_importedData, true)); } catch { } }

        private void MarkAsImported(string itemId, string pkgName, List<string> importedFiles = null, long fileSize = 0)
        {
            if (_importedData == null) _importedData = new VLAImportedData();
            var e = _importedData.imported_items.FirstOrDefault(x => x.item_id == itemId);
            if (e != null)
            {
                if (!e.imported_packages.Contains(pkgName)) e.imported_packages.Add(pkgName);
                e.last_imported_at = DateTime.Now.ToString("o");
                if (importedFiles != null && importedFiles.Count > 0) e.imported_files = importedFiles;
                if (fileSize > 0) e.package_file_size = fileSize;
            }
            else
            {
                _importedData.imported_items.Add(new VLAImportedEntry
                {
                    item_id = itemId,
                    imported_packages = new List<string> { pkgName },
                    last_imported_at = DateTime.Now.ToString("o"),
                    imported_files = importedFiles ?? new List<string>(),
                    package_file_size = fileSize
                });
            }
            SaveImportedData(); Repaint();
        }

        private bool IsImported(string itemId) => _importedData?.imported_items.Any(e => e.item_id == itemId) == true;

        /// <summary>指定パッケージがインポート済みかどうか</summary>
        private bool IsPackageImported(string itemId, string packageName)
        {
            if (string.IsNullOrEmpty(packageName)) return IsImported(itemId);
            var entry = _importedData?.imported_items.FirstOrDefault(e => e.item_id == itemId);
            return entry?.imported_packages.Contains(packageName) == true;
        }

        /// <summary>全パッケージがインポート済みか（複数パッケージアイテム用）</summary>
        private bool IsAllPackagesImported(string itemId, List<string> packageNames)
        {
            if (packageNames == null || packageNames.Count == 0) return false;
            var entry = _importedData?.imported_items.FirstOrDefault(e => e.item_id == itemId);
            if (entry == null) return false;
            return packageNames.All(p => entry.imported_packages.Contains(p));
        }

        /// <summary>インポート済みパッケージ数 / 全パッケージ数</summary>
        private (int imported, int total) GetImportedPackageCounts(string itemId, List<string> packageNames)
        {
            if (packageNames == null || packageNames.Count == 0) return (0, 0);
            var entry = _importedData?.imported_items.FirstOrDefault(e => e.item_id == itemId);
            if (entry == null) return (0, packageNames.Count);
            int imported = packageNames.Count(p => entry.imported_packages.Contains(p));
            return (imported, packageNames.Count);
        }

        /// <summary>インポート済みファイルが実際にプロジェクト内に存在するか検証</summary>
        private bool VerifyImportedFilesExist(string itemId)
        {
            var entry = _importedData?.imported_items.FirstOrDefault(e => e.item_id == itemId);
            if (entry == null || entry.imported_files == null || entry.imported_files.Count == 0)
                return true; // ファイル情報なし → 検証不可 → 存在扱い

            string projectPath = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
            // 最初の数ファイルだけチェック（パフォーマンス考慮）
            int checkCount = Math.Min(entry.imported_files.Count, 5);
            int missingCount = 0;
            for (int i = 0; i < checkCount; i++)
            {
                string fullPath = Path.Combine(projectPath, entry.imported_files[i]);
                if (!File.Exists(fullPath)) missingCount++;
            }
            // 半数以上が消えていたら「削除された」と判定
            return missingCount < (checkCount + 1) / 2;
        }

        /// <summary>インポート済みファイルの一番上のフォルダをUnityで選択・ハイライトする</summary>
        private void FindImportedAssets(string itemId)
        {
            var entry = _importedData?.imported_items.FirstOrDefault(e => e.item_id == itemId);
            if (entry == null || entry.imported_files == null || entry.imported_files.Count == 0)
            {
                Debug.LogWarning("[VLA Bridge] インポート済みファイル情報がありません。再インポートすると記録されます。");
                return;
            }

            // imported_files から Assets/ 直下のサブフォルダを集計し、最も多いフォルダを選ぶ
            // 例: "Assets/AuthorName/AssetName/Materials/x.mat" → "Assets/AuthorName" をカウント
            // "Assets" 自体は絶対に選ばない — 必ず Assets/X 以下を提示する
            var folderCounts = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);

            foreach (var f in entry.imported_files)
            {
                string normalized = f.Replace("\\", "/");
                if (!normalized.StartsWith("Assets/")) continue;
                string[] parts = normalized.Split('/');
                // parts[0] = "Assets", parts[1] = 直下フォルダ, parts[2] = その下...
                // 最低でも Assets/X の形（parts.Length >= 3 はファイルがフォルダ内にある）
                if (parts.Length < 2) continue;

                // Assets/X を候補にする（X がファイルならスキップ）
                string subFolder = "Assets/" + parts[1];
                if (AssetDatabase.IsValidFolder(subFolder))
                {
                    folderCounts.TryGetValue(subFolder, out int c);
                    folderCounts[subFolder] = c + 1;
                }
                // もう少し深い Assets/X/Y も候補に（より具体的なフォルダがあれば優先）
                if (parts.Length >= 3)
                {
                    string deepFolder = "Assets/" + parts[1] + "/" + parts[2];
                    if (AssetDatabase.IsValidFolder(deepFolder))
                    {
                        folderCounts.TryGetValue(deepFolder, out int c2);
                        folderCounts[deepFolder] = c2 + 1;
                    }
                }
            }

            // 最も多くのファイルを含むフォルダを選択（深い方を優先、同数なら深い方）
            string bestFolder = null;
            int bestScore = 0;
            int bestDepth = 0;
            foreach (var kvp in folderCounts)
            {
                int depth = kvp.Key.Split('/').Length;
                // スコア: ファイル数が多い方が良い。同数なら深い方が具体的で良い
                if (kvp.Value > bestScore || (kvp.Value == bestScore && depth > bestDepth))
                {
                    bestFolder = kvp.Key;
                    bestScore = kvp.Value;
                    bestDepth = depth;
                }
            }

            // フォルダが見つからなければ、ファイルパスから親フォルダを推定
            if (bestFolder == null)
            {
                foreach (var f in entry.imported_files)
                {
                    string normalized = f.Replace("\\", "/");
                    if (!normalized.StartsWith("Assets/")) continue;
                    string dir = Path.GetDirectoryName(normalized)?.Replace("\\", "/");
                    // "Assets" 自体は除外
                    if (!string.IsNullOrEmpty(dir) && dir != "Assets" && AssetDatabase.IsValidFolder(dir))
                    {
                        bestFolder = dir;
                        break;
                    }
                }
            }

            if (bestFolder != null)
            {
                // フォルダを選択するだけだと Project 窓は親フォルダの中身を表示してしまい、
                // 目的フォルダの中身が見えない。そこでフォルダ内の最初のアセットを ping して
                // Project 窓にフォルダの中身を展開表示させる。
                var obj = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(bestFolder);
                if (obj != null)
                {
                    // フォルダ内の最初のサブアセット（ファイルまたはサブフォルダ）を探す
                    string[] childGuids = AssetDatabase.FindAssets("", new[] { bestFolder });
                    UnityEngine.Object childToReveal = null;
                    foreach (var guid in childGuids)
                    {
                        string childPath = AssetDatabase.GUIDToAssetPath(guid);
                        // bestFolder 直下の子だけ対象（孫以下は除外）
                        if (string.IsNullOrEmpty(childPath) || childPath == bestFolder) continue;
                        string relative = childPath.Substring(bestFolder.Length + 1);
                        if (relative.Contains("/")) continue; // 孫以下はスキップ
                        childToReveal = AssetDatabase.LoadAssetAtPath<UnityEngine.Object>(childPath);
                        if (childToReveal != null) break;
                    }

                    if (childToReveal != null)
                    {
                        // 子アセットを選択 → Project 窓がフォルダの中身を表示する
                        Selection.activeObject = childToReveal;
                        EditorGUIUtility.PingObject(childToReveal);
                    }
                    else
                    {
                        // 子が見つからなければフォルダ自体を選択（フォールバック）
                        Selection.activeObject = obj;
                        EditorGUIUtility.PingObject(obj);
                    }
                    Debug.Log($"[VLA Bridge] 📂 {bestFolder}");
                }
            }
            else
            {
                Debug.LogWarning("[VLA Bridge] インポート先フォルダが見つかりません。削除された可能性があります。");
            }
        }

        private string GetImportedDataPath() => Path.Combine(Application.dataPath, "..", "ProjectSettings", "VLABridgeImported.json");
        private static string GetProjectPath() => Path.GetFullPath(Path.Combine(Application.dataPath, ".."));

        // ═══════════════════════════════════════════
        // GUI
        // ═══════════════════════════════════════════
        private void OnGUI()
        {
            DrawToolbar(); DrawStatusBar();
            if (_isExportingTemplate) { DrawOverlay("📦 テンプレート作成中…", _exportTemplateName, _exportProgressMessage); return; }
            if (_isWaitingImport) { DrawOverlay("📦 インポート準備中…", _waitingImportItemName, "VLA がパッケージを処理しています。"); return; }
            if (_isWaitingUnarchive)
            {
                string desc = !string.IsNullOrEmpty(_unarchiveProgressMessage)
                    ? _unarchiveProgressMessage
                    : "VLA がアーカイブを展開しています。しばらくお待ちください。";
                string title = _unarchiveProgressStatus switch
                {
                    "starting"   => "📦 解凍準備中…",
                    "extracting" => "📦 ZIP 解凍中…",
                    "finalizing" => "✅ 展開完了、更新中…",
                    _            => "📦 ZIP 解凍中…"
                };
                DrawOverlay(title, _waitingUnarchiveItemName, desc);
                return;
            }
            if (_connState != ConnectionState.Connected) { DrawDisconnectedView(); return; }
            DrawLibraryCards();
        }

        private void DrawToolbar()
        {
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("🔍", GUILayout.Width(20));
            _searchFilter = EditorGUILayout.TextField(_searchFilter, EditorStyles.toolbarSearchField, GUILayout.MinWidth(80));
            if (GUILayout.Button("✕", EditorStyles.toolbarButton, GUILayout.Width(20))) _searchFilter = "";
            GUILayout.Space(4);
            if (_favorites.Count > 0)
            {
                var n = new string[_favorites.Count + 1]; n[0] = "⭐ すべて";
                for (int i = 0; i < _favorites.Count; i++) n[i + 1] = "⭐ " + (_favorites[i].name.Length > 10 ? _favorites[i].name.Substring(0, 10) + "…" : _favorites[i].name);
                _selectedFavoriteIndex = EditorGUILayout.Popup(_selectedFavoriteIndex, n, EditorStyles.toolbarPopup, GUILayout.Width(110));
            }
            GUILayout.FlexibleSpace();
            var ph = _hideImported; _hideImported = GUILayout.Toggle(_hideImported, "済を隠す", EditorStyles.toolbarButton, GUILayout.Width(70)); if (ph != _hideImported) Repaint();
            if (GUILayout.Button("↺", EditorStyles.toolbarButton, GUILayout.Width(24))) { SendAsync("{\"type\":\"get_unity_library\"}"); SendAsync("{\"type\":\"get_favorites\"}"); }
            if (GUILayout.Button("⚙", EditorStyles.toolbarButton, GUILayout.Width(24))) VLABridgeSettings.ShowWindow(_wsUrl, u => { _wsUrl = u; EditorPrefs.SetString(PREFS_WS_URL, _wsUrl); Disconnect(); _hasConnectedOnce = false; ConnectAsync(); });
            EditorGUILayout.EndHorizontal();
        }

        private void DrawStatusBar()
        {
            var c = _connState switch { ConnectionState.Connected => new Color(0.2f, 0.6f, 0.3f, 0.3f), ConnectionState.Connecting => new Color(0.8f, 0.6f, 0.2f, 0.3f), _ => new Color(0.3f, 0.3f, 0.3f, 0.3f) };
            var pb = GUI.backgroundColor; GUI.backgroundColor = c; EditorGUILayout.BeginHorizontal("box"); GUI.backgroundColor = pb;
            GUILayout.Label($"{(_connState switch { ConnectionState.Connected => "🟢", ConnectionState.Connecting => "🟡", _ => "🔴" })} {_statusMessage}", EditorStyles.miniLabel);
            EditorGUILayout.EndHorizontal();
        }

        private void DrawOverlay(string title, string itemName, string desc)
        {
            GUILayout.FlexibleSpace(); EditorGUILayout.BeginVertical(); GUILayout.FlexibleSpace();
            GUILayout.Label(title, new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = 14 });
            GUILayout.Space(6);
            var ss = new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, wordWrap = true };
            GUILayout.Label(itemName, ss); GUILayout.Space(4); GUILayout.Label(desc, ss);
            GUILayout.FlexibleSpace(); EditorGUILayout.EndVertical(); GUILayout.FlexibleSpace();
        }

        private void DrawDisconnectedView()
        {
            GUILayout.FlexibleSpace(); EditorGUILayout.BeginVertical(); GUILayout.FlexibleSpace();
            GUILayout.Label("VLA (VRC-Library Assist) に接続できません", new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = 14, wordWrap = true });
            GUILayout.Space(8); GUILayout.Label("VLA を起動してください。自動的に再接続を試みます。", new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, wordWrap = true });
            GUILayout.Space(16); EditorGUILayout.BeginHorizontal(); GUILayout.FlexibleSpace();
            if (GUILayout.Button("再接続", GUILayout.Width(120), GUILayout.Height(28))) ConnectAsync();
            GUILayout.FlexibleSpace(); EditorGUILayout.EndHorizontal();
            GUILayout.FlexibleSpace(); EditorGUILayout.EndVertical(); GUILayout.FlexibleSpace();
        }

        private void DrawLibraryCards()
        {
            var items = GetFilteredItems();
            if (items.Count == 0) { GUILayout.FlexibleSpace(); GUILayout.Label(_libraryItems.Count == 0 ? "ライブラリにアイテムがありません" : "条件に一致するアイテムがありません", new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = 12 }); GUILayout.FlexibleSpace(); return; }

            int readyCount = items.Count(i => !i.is_archived);
            int archivedCount = items.Count - readyCount;

            _scrollPosition = EditorGUILayout.BeginScrollView(_scrollPosition);
            int cols = Mathf.Max(1, Mathf.FloorToInt((position.width - 20) / (CARD_WIDTH + 8)));

            // ── インポート可能セクション ──
            if (readyCount > 0)
            {
                var headerStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 11 };
                EditorGUILayout.LabelField($"🎮 インポート可能 ({readyCount})", headerStyle);
                GUILayout.Space(2);
                int col = 0;
                EditorGUILayout.BeginHorizontal();
                foreach (var item in items)
                {
                    if (item.is_archived) continue;
                    DrawCard(item); if (++col >= cols) { col = 0; EditorGUILayout.EndHorizontal(); EditorGUILayout.BeginHorizontal(); }
                }
                for (int i = col; i < cols && col > 0; i++) GUILayout.Space(CARD_WIDTH + 8);
                EditorGUILayout.EndHorizontal();
            }

            // ── アーカイブ（ZIP）セクション ──
            if (archivedCount > 0)
            {
                GUILayout.Space(8);
                var zipHeaderStyle = new GUIStyle(EditorStyles.boldLabel) { fontSize = 11, normal = { textColor = new Color(0.6f, 0.6f, 0.6f) } };
                EditorGUILayout.LabelField($"📦 ZIP（VLAで解凍が必要） ({archivedCount})", zipHeaderStyle);
                GUILayout.Space(2);
                int col = 0;
                EditorGUILayout.BeginHorizontal();
                foreach (var item in items)
                {
                    if (!item.is_archived) continue;
                    DrawCard(item); if (++col >= cols) { col = 0; EditorGUILayout.EndHorizontal(); EditorGUILayout.BeginHorizontal(); }
                }
                for (int i = col; i < cols && col > 0; i++) GUILayout.Space(CARD_WIDTH + 8);
                EditorGUILayout.EndHorizontal();
            }

            EditorGUILayout.EndScrollView();
        }

        private void DrawCard(VLALibraryItem item)
        {
            bool imported = IsImported(item.item_id);
            bool archived = item.is_archived;
            var pk = item.unity_package_names;
            bool multi = pk != null && pk.Count > 1;

            // 複数パッケージの場合: 部分インポート vs 全インポート
            var (impCount, totalCount) = GetImportedPackageCounts(item.item_id, pk);
            bool partialImport = imported && multi && impCount > 0 && impCount < totalCount;
            bool allImported = imported && (!multi || impCount >= totalCount);

            // インポート済みだがファイルが消されている場合は「!」バッジ
            bool importBroken = imported && !VerifyImportedFilesExist(item.item_id);

            // 現在選択中のパッケージがインポート済みか
            bool selectedPkgImported = false;
            string selectedPkgName = "";
            if (!archived && pk != null && pk.Count > 0)
            {
                int si = 0;
                if (multi && _selectedPackageIndex.TryGetValue(item.item_id, out int idx)) si = Math.Min(idx, pk.Count - 1);
                selectedPkgName = pk[si];
                selectedPkgImported = IsPackageImported(item.item_id, selectedPkgName);
            }

            // アーカイブ済みはカード全体を薄くする
            if (archived) GUI.color = new Color(1f, 1f, 1f, 0.55f);

            EditorGUILayout.BeginVertical("box", GUILayout.Width(CARD_WIDTH), GUILayout.Height(CARD_HEIGHT));

            // サムネイル
            Rect tr = GUILayoutUtility.GetRect(THUMB_SIZE, THUMB_SIZE - 20, GUILayout.Width(THUMB_SIZE), GUILayout.Height(THUMB_SIZE - 20));
            if (_thumbnailCache.TryGetValue(item.item_id, out var tex) && tex != null) GUI.DrawTexture(tr, tex, ScaleMode.ScaleToFit);
            else { EditorGUI.DrawRect(tr, new Color(0.15f, 0.15f, 0.2f)); GUI.Label(tr, "読込中…", new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter }); }

            // バッジ — アーカイブ時は不透明度を戻してバッジを見やすく
            if (archived) GUI.color = Color.white;

            if (importBroken)
            {
                var br = new Rect(tr.xMax - 24, tr.y + 2, 22, 16);
                EditorGUI.DrawRect(br, new Color(0.9f, 0.5f, 0.1f, 0.9f));
                GUI.Label(br, "!", new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white }, fontSize = 10, fontStyle = FontStyle.Bold });
            }
            else if (partialImport)
            {
                // 部分インポート — 青系バッジ + カウント
                var br = new Rect(tr.xMax - 38, tr.y + 2, 36, 16);
                EditorGUI.DrawRect(br, new Color(0.3f, 0.55f, 0.85f, 0.9f));
                GUI.Label(br, $"{impCount}/{totalCount}", new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white }, fontSize = 9, fontStyle = FontStyle.Bold });
            }
            else if (allImported)
            {
                var br = new Rect(tr.xMax - 24, tr.y + 2, 22, 16);
                EditorGUI.DrawRect(br, new Color(0.2f, 0.7f, 0.3f, 0.9f));
                GUI.Label(br, "✓", new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white }, fontSize = 9 });
            }
            if (archived)
            {
                var ar = new Rect(tr.x + 2, tr.y + 2, 28, 14);
                EditorGUI.DrawRect(ar, new Color(0.9f, 0.65f, 0.1f, 0.85f));
                GUI.Label(ar, "ZIP", new GUIStyle(EditorStyles.miniLabel) { alignment = TextAnchor.MiddleCenter, normal = { textColor = Color.white }, fontSize = 8, fontStyle = FontStyle.Bold });
            }

            // アーカイブ済みはテキストも薄く
            if (archived) GUI.color = new Color(1f, 1f, 1f, 0.55f);

            // 名前
            string dn = item.item_name; if (dn.Length > 30) dn = dn.Substring(0, 27) + "…";
            EditorGUILayout.LabelField(dn, new GUIStyle(EditorStyles.miniLabel) { wordWrap = true, fontSize = 10 }, GUILayout.Height(28));

            // GUI.color を戻してからボタン描画
            GUI.color = Color.white;

            // パッケージ選択（非アーカイブ・複数時のみ）— インポート済みパッケージに✓を付ける
            if (multi && !archived)
            {
                if (!_selectedPackageIndex.ContainsKey(item.item_id)) _selectedPackageIndex[item.item_id] = 0;
                int idx = _selectedPackageIndex[item.item_id]; if (idx >= pk.Count) idx = 0;
                var labels = pk.Select(n =>
                {
                    string label = n.Length > 20 ? n.Substring(0, 17) + "…" : n;
                    if (IsPackageImported(item.item_id, n)) label = "✓ " + label;
                    return label;
                }).ToArray();
                _selectedPackageIndex[item.item_id] = EditorGUILayout.Popup(idx, labels, GUILayout.Height(16));
            }

            if (archived)
            {
                // ═══ ZIP状態 → グレーアウト＋解凍ボタン（VLAアプリ側で展開） ═══
                GUI.enabled = !_isWaitingImport && !_isWaitingUnarchive;
                GUI.backgroundColor = new Color(0.55f, 0.55f, 0.55f);
                if (GUILayout.Button("📦 VLAで解凍", GUILayout.Height(20)))
                    RequestUnarchive(item);
                GUI.backgroundColor = Color.white;
                GUI.enabled = true;
            }
            else
            {
                // ═══ 通常 → Import / Find ボタン ═══
                GUI.enabled = !_isWaitingImport && !_isWaitingUnarchive;

                if (imported && !importBroken)
                {
                    // インポート済み → Import + Find を横並び
                    EditorGUILayout.BeginHorizontal();
                    // Import ボタン
                    GUI.backgroundColor = selectedPkgImported ? new Color(0.3f, 0.5f, 0.3f) : new Color(0.3f, 0.7f, 0.3f);
                    string btnLabel = selectedPkgImported ? "✓ 再Import" : "🎮 Import";
                    if (GUILayout.Button(btnLabel, GUILayout.Height(20)))
                    {
                        string sp = "";
                        if (multi && _selectedPackageIndex.TryGetValue(item.item_id, out int si) && si >= 0 && si < pk.Count) sp = pk[si];
                        else if (pk != null && pk.Count == 1) sp = pk[0];
                        RequestImportPackage(item, sp);
                    }
                    // Find ボタン
                    GUI.backgroundColor = new Color(0.4f, 0.55f, 0.75f);
                    if (GUILayout.Button("📂", GUILayout.Width(28), GUILayout.Height(20)))
                        FindImportedAssets(item.item_id);
                    GUI.backgroundColor = Color.white;
                    EditorGUILayout.EndHorizontal();
                }
                else
                {
                    // 未インポート or 壊れている → Import ボタンのみ
                    GUI.backgroundColor = new Color(0.3f, 0.7f, 0.3f);
                    string btnLabel = importBroken ? "⚠ 再Import" : "🎮 Import";
                    if (GUILayout.Button(btnLabel, GUILayout.Height(20)))
                    {
                        string sp = "";
                        if (multi && _selectedPackageIndex.TryGetValue(item.item_id, out int si) && si >= 0 && si < pk.Count) sp = pk[si];
                        else if (pk != null && pk.Count == 1) sp = pk[0];
                        RequestImportPackage(item, sp);
                    }
                    GUI.backgroundColor = Color.white;
                }

                GUI.enabled = true;
            }

            EditorGUILayout.EndVertical();
        }

        private List<VLALibraryItem> GetFilteredItems()
        {
            HashSet<string> favIds = null;
            if (_selectedFavoriteIndex > 0 && _selectedFavoriteIndex <= _favorites.Count)
                favIds = new HashSet<string>(_favorites[_selectedFavoriteIndex - 1].item_ids ?? new List<string>());
            var ready = new List<VLALibraryItem>();
            var archived = new List<VLALibraryItem>();
            foreach (var item in _libraryItems)
            {
                if (_hideImported && IsImported(item.item_id)) continue;
                if (favIds != null && !favIds.Contains(item.item_id)) continue;
                if (!string.IsNullOrEmpty(_searchFilter) && !item.item_name.ToLower().Contains(_searchFilter.ToLower()) && !item.author_name.ToLower().Contains(_searchFilter.ToLower())) continue;
                if (item.is_archived) archived.Add(item);
                else ready.Add(item);
            }
            // 非アーカイブ（インポート可能）を先頭に、アーカイブ（ZIP）を後ろに
            ready.AddRange(archived);
            return ready;
        }

        /// <summary>テンプレート保存先ディレクトリを取得</summary>
        private string GetTemplateOutputDir()
        {
            string basePath = _vlaSavePath;
            if (string.IsNullOrEmpty(basePath))
                basePath = Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "VRC-Library");
            return Path.Combine(basePath, "_VLA_templates");
        }

        /// <summary>VLA 保存先パスを取得（Template Creator ポップアップから呼ばれる）</summary>
        public string GetVlaSavePath() => _vlaSavePath;

        // ── VLATemplateImportWindow から呼ぶ公開メソッド ──

        /// <summary>VLA にテンプレート一覧リクエストを送信する</summary>
        public void SendTemplateListRequest()
            => SendAsync("{\"type\":\"get_template_list\"}");

        /// <summary>VLA に指定テンプレートのインポートリクエストを送信する</summary>
        public void SendTemplateImportRequest(string templateId)
        {
            SendAsync(JsonUtility.ToJson(new TemplateImportRequestMessage
            {
                type         = "request_template_import",
                template_id  = templateId,
                project_path = GetProjectPath()
            }));
        }

        /// <summary>VLA にテンプレートインポートの結果を通知する</summary>
        public void SendTemplateImportResult(string itemId, string pkgName, bool success, string error)
        {
            SendAsync(JsonUtility.ToJson(new ImportResultMessage
            {
                type         = "import_package_result",
                item_id      = itemId,
                package_name = pkgName,
                success      = success,
                error        = error,
                project_path = GetProjectPath()
            }));
        }

        // テンプレート作成完了コールバック
        private Action<bool, string> _templateExportCallback;

        /// <summary>テンプレート作成を開始（Template Creator ポップアップから呼ばれる）</summary>
        public void RequestTemplateExport(string templateName, string memo, Action<bool, string> onComplete)
        {
            _templateExportCallback = onComplete;
            ExportTemplate(templateName, memo);
        }

        // ═══════════════════════════════════════════
        // テンプレートエクスポート処理
        // ═══════════════════════════════════════════
        private void ExportTemplate(string templateName, string memo)
        {
            if (string.IsNullOrWhiteSpace(templateName))
            {
                Debug.LogWarning("[VLA Bridge] テンプレート名が空のためエクスポートを中止しました。");
                _templateExportCallback?.Invoke(false, "テンプレート名が空です");
                _templateExportCallback = null;
                return;
            }

            _isExportingTemplate = true;
            _exportProgressMessage = "エクスポートを準備中…";
            Repaint();

            try
            {
                string safeName = SanitizeFileName(templateName);
                string timestamp = DateTime.Now.ToString("yyyyMMdd_HHmmss");
                string templateId = $"{safeName}_{timestamp}";
                string outputDir = Path.Combine(GetTemplateOutputDir(), templateId);
                Directory.CreateDirectory(outputDir);

                // 1) Unity エディタ画面をキャプチャ
                _exportProgressMessage = "エディタ画面をキャプチャ中…";
                Repaint();
                string previewPath = Path.Combine(outputDir, "preview.png");
                CaptureEditorScreenshot(previewPath);

                // 2) Assets 全体を .unitypackage にエクスポート（依存含む）
                _exportProgressMessage = "Assets を .unitypackage にエクスポート中…\n（大きなプロジェクトでは数分かかります）";
                Repaint();
                string packagePath = Path.Combine(outputDir, $"{safeName}.unitypackage");
                ExportAllAssetsAsPackage(packagePath);

                // 3) Packages/manifest.json をコピー（UPM パッケージ情報）
                _exportProgressMessage = "Packages 情報をコピー中…";
                Repaint();
                CopyPackagesManifest(outputDir);

                // 4) ProjectSettings をコピー（レンダリング・タグ・レイヤー等）
                _exportProgressMessage = "ProjectSettings をコピー中…";
                Repaint();
                CopyProjectSettings(outputDir);

                // 5) メタ情報 JSON を保存
                _exportProgressMessage = "メタ情報を保存中…";
                Repaint();
                var meta = new VLATemplateMeta
                {
                    template_id = templateId,
                    template_name = templateName,
                    memo = memo,
                    created_at = DateTime.Now.ToString("o"),
                    scene_name = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name,
                    project_name = Application.productName,
                    unity_version = Application.unityVersion,
                    preview_file = "preview.png",
                    package_file = $"{safeName}.unitypackage",
                    has_packages_manifest = true,
                    has_project_settings = true
                };
                string metaJson = JsonUtility.ToJson(meta, true);
                File.WriteAllText(Path.Combine(outputDir, "template_meta.json"), metaJson);

                // 6) VLA に完了通知
                SendAsync(JsonUtility.ToJson(new TemplateExportedMessage
                {
                    type = "template_export_result",
                    template_id = templateId,
                    template_name = templateName,
                    template_path = outputDir,
                    success = true,
                    error = ""
                }));

                _isExportingTemplate = false;
                _exportProgressMessage = "";
                Repaint();

                Debug.Log($"[VLA Bridge] テンプレート作成完了: {outputDir}");
                _templateExportCallback?.Invoke(true, "");
                _templateExportCallback = null;
            }
            catch (Exception ex)
            {
                _isExportingTemplate = false;
                _exportProgressMessage = "";
                Repaint();
                Debug.LogError($"[VLA Bridge] テンプレート作成失敗: {ex.Message}\n{ex.StackTrace}");
                SendAsync(JsonUtility.ToJson(new TemplateExportedMessage
                    { type = "template_export_result", template_id = "", template_name = templateName, success = false, error = ex.Message }));
                _templateExportCallback?.Invoke(false, ex.Message);
                _templateExportCallback = null;
            }
        }

        /// <summary>Assets 内の全アセットを .unitypackage としてエクスポート（依存関係・シーン・マテリアル・スクリプト含む）</summary>
        private void ExportAllAssetsAsPackage(string outputPath)
        {
            // Assets 以下のすべてのアセットを収集
            string[] allGuids = AssetDatabase.FindAssets("", new[] { "Assets" });
            var assetPaths = new HashSet<string>();

            foreach (var guid in allGuids)
            {
                string path = AssetDatabase.GUIDToAssetPath(guid);
                if (!string.IsNullOrEmpty(path) && path.StartsWith("Assets/"))
                    assetPaths.Add(path);
            }

            // Packages/ 内のローカルパッケージも含める（embeddedやlocalで参照されているもの）
            string packagesDir = Path.Combine(Path.GetDirectoryName(Application.dataPath), "Packages");
            if (Directory.Exists(packagesDir))
            {
                foreach (var dir in Directory.GetDirectories(packagesDir))
                {
                    // package.json があるフォルダはローカルパッケージ
                    if (File.Exists(Path.Combine(dir, "package.json")))
                    {
                        string pkgName = Path.GetFileName(dir);
                        string pkgAssetsPath = $"Packages/{pkgName}";
                        string[] pkgGuids = AssetDatabase.FindAssets("", new[] { pkgAssetsPath });
                        foreach (var guid in pkgGuids)
                        {
                            string path = AssetDatabase.GUIDToAssetPath(guid);
                            if (!string.IsNullOrEmpty(path))
                                assetPaths.Add(path);
                        }
                    }
                }
            }

            if (assetPaths.Count == 0)
            {
                Debug.LogWarning("[VLA Bridge] エクスポート対象のアセットが見つかりません。");
                return;
            }

            AssetDatabase.ExportPackage(
                assetPaths.ToArray(),
                outputPath,
                ExportPackageOptions.Recurse | ExportPackageOptions.IncludeDependencies);

            Debug.Log($"[VLA Bridge] .unitypackage エクスポート完了: {assetPaths.Count} アセット → {outputPath}");
        }

        /// <summary>Packages/manifest.json と packages-lock.json をコピー</summary>
        private void CopyPackagesManifest(string outputDir)
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string packagesDir = Path.Combine(outputDir, "Packages");
            Directory.CreateDirectory(packagesDir);

            string manifest = Path.Combine(projectRoot, "Packages", "manifest.json");
            if (File.Exists(manifest))
                File.Copy(manifest, Path.Combine(packagesDir, "manifest.json"), true);

            string lockFile = Path.Combine(projectRoot, "Packages", "packages-lock.json");
            if (File.Exists(lockFile))
                File.Copy(lockFile, Path.Combine(packagesDir, "packages-lock.json"), true);

            Debug.Log($"[VLA Bridge] Packages manifest コピー完了 → {packagesDir}");
        }

        /// <summary>ProjectSettings フォルダを丸ごとコピー（タグ・レイヤー・レンダリング設定等）</summary>
        private void CopyProjectSettings(string outputDir)
        {
            string projectRoot = Path.GetDirectoryName(Application.dataPath);
            string srcSettings = Path.Combine(projectRoot, "ProjectSettings");
            string dstSettings = Path.Combine(outputDir, "ProjectSettings");

            if (!Directory.Exists(srcSettings)) return;
            Directory.CreateDirectory(dstSettings);

            foreach (var file in Directory.GetFiles(srcSettings, "*", SearchOption.AllDirectories))
            {
                string relativePath = file.Substring(srcSettings.Length + 1);
                string destPath = Path.Combine(dstSettings, relativePath);
                string destDir = Path.GetDirectoryName(destPath);
                if (!Directory.Exists(destDir)) Directory.CreateDirectory(destDir);
                File.Copy(file, destPath, true);
            }

            Debug.Log($"[VLA Bridge] ProjectSettings コピー完了 → {dstSettings}");
        }

        /// <summary>シーン内のすべてのオブジェクトが映るように360°パノラマ画像を撮影</summary>
        /// <summary>
        /// Unity エディタの現在のゲームビュー（またはシーンビュー）を
        /// スクリーンショットとしてキャプチャし PNG ファイルに保存する。
        ///
        /// v2.1: 360°パノラマ撮影を廃止し、エディタ画面の直接キャプチャに変更。
        /// - ScreenCapture.CaptureScreenshotAsTexture() でゲームビューをキャプチャ
        /// - ゲームビューが閉じている場合は EditorWindow から SceneView を取得して代替
        /// - さらに取得できない場合は Application.dataPath 付近のデフォルト画像をコピーするか
        ///   プレースホルダーを生成する（エラー終了はしない）
        /// </summary>
        private void CaptureEditorScreenshot(string outputPath)
        {
            Texture2D captured = null;
            try
            {
                // ① ゲームビューをキャプチャ（Editor 専用 API）
                captured = ScreenCapture.CaptureScreenshotAsTexture(superSize: 1);
            }
            catch { }

            if (captured == null)
            {
                // ② SceneView のカメラからキャプチャ（ゲームビューが開いていない場合）
                var sceneView = SceneView.lastActiveSceneView;
                if (sceneView != null && sceneView.camera != null)
                {
                    var cam = sceneView.camera;
                    int w   = Mathf.Max(1, (int)sceneView.position.width);
                    int h   = Mathf.Max(1, (int)sceneView.position.height);
                    var rt  = new RenderTexture(w, h, 24, RenderTextureFormat.ARGB32);
                    var prevRT = cam.targetTexture;
                    cam.targetTexture = rt;
                    cam.Render();
                    cam.targetTexture = prevRT;

                    RenderTexture.active = rt;
                    captured = new Texture2D(w, h, TextureFormat.RGB24, false);
                    captured.ReadPixels(new Rect(0, 0, w, h), 0, 0);
                    captured.Apply();
                    RenderTexture.active = null;
                    UnityEngine.Object.DestroyImmediate(rt);
                }
            }

            if (captured != null)
            {
                try
                {
                    byte[] png = captured.EncodeToPNG();
                    File.WriteAllBytes(outputPath, png);
                    Debug.Log($"[VLA Bridge] エディタスクリーンショット保存: {outputPath} ({captured.width}x{captured.height})");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[VLA Bridge] スクリーンショット保存失敗: {ex.Message}");
                }
                finally
                {
                    UnityEngine.Object.DestroyImmediate(captured);
                }
            }
            else
            {
                // ③ キャプチャできない場合は最小限のプレースホルダーを生成
                try
                {
                    var placeholder = new Texture2D(320, 180, TextureFormat.RGB24, false);
                    var cols = new Color[320 * 180];
                    var bg   = new Color(0.12f, 0.12f, 0.18f);
                    for (int i = 0; i < cols.Length; i++) cols[i] = bg;
                    placeholder.SetPixels(cols);
                    placeholder.Apply();
                    File.WriteAllBytes(outputPath, placeholder.EncodeToPNG());
                    UnityEngine.Object.DestroyImmediate(placeholder);
                    Debug.LogWarning("[VLA Bridge] キャプチャ不可のためプレースホルダー画像を生成しました。");
                }
                catch (Exception ex)
                {
                    Debug.LogWarning($"[VLA Bridge] プレースホルダー生成失敗: {ex.Message}");
                }
            }
        }

        private static string SanitizeFileName(string name)
        {
            char[] invalid = Path.GetInvalidFileNameChars();
            var sb = new StringBuilder(name.Length);
            foreach (char c in name)
                sb.Append(Array.IndexOf(invalid, c) >= 0 ? '_' : c);
            string result = sb.ToString().Trim();
            if (string.IsNullOrEmpty(result)) result = "template";
            return result;
        }

        // ═══════════════════════════════════════════
        // JSON 型
        // ═══════════════════════════════════════════
        [Serializable] private class WsMessage { public string type; public string item_id; public string project_name; public string project_path; public string package_name; }
        [Serializable] private class ThumbnailResponse { public string type; public string item_id; public string thumbnail_base64; }
        [Serializable] private class ImportResultMessage { public string type; public string item_id; public string package_name; public bool success; public string error; public string project_path; }
        [Serializable] private class PackageFileReadyMessage { public string type; public string item_id; public string package_name; public string file_path; }
        [Serializable] private class FavoritesResponse { public string type; public List<VLAFavoriteList> favorites; }
        [Serializable] private class UnarchiveResultMessage { public string type; public string item_id; public bool success; public string error; }
        [Serializable] private class UnarchiveProgressMessage { public string type; public string item_id; public string status; public string message; }
        [Serializable] private class SavePathResponse { public string type; public string path; }
        [Serializable] private class TemplateExportedMessage { public string type; public string template_id; public string template_name; public string template_path; public bool success; public string error; }
        [Serializable] private class TemplateImportRequestMessage { public string type; public string template_id; public string project_path; }
    }

    // ── テンプレート関連型 ──
    [Serializable]
    public class VLATemplateMeta
    {
        public string template_id;
        public string template_name;
        public string memo;
        public string created_at;
        public string scene_name;
        public string project_name;
        public string unity_version;
        /// <summary>エディタスクリーンショットのファイル名（旧 panorama_file から変更）</summary>
        public string preview_file;
        public string package_file;
        public bool   has_packages_manifest;
        public bool   has_project_settings;
    }

    [Serializable] public class VLALibraryItem { public string item_id; public string item_name; public string author_name; public string thumbnail_url; public int unity_package_count; public List<string> unity_package_names; public bool is_archived; }
    [Serializable] public class UnityLibraryResponse { public string type; public List<VLALibraryItem> items; }
    [Serializable] public class VLAImportedData { public string project_path; public List<VLAImportedEntry> imported_items = new List<VLAImportedEntry>(); }
    [Serializable] public class VLAImportedEntry { public string item_id; public List<string> imported_packages = new List<string>(); public string last_imported_at; public List<string> imported_files = new List<string>(); public long package_file_size; }
    [Serializable] public class VLAFavoriteList { public string id; public string name; public List<string> item_ids = new List<string>(); }

    // ═══════════════════════════════════════════════════════════
    // VLA Template Creator — 独立ポップアップウィンドウ
    // ═══════════════════════════════════════════════════════════
    public class VLATemplateCreatorWindow : EditorWindow
    {
        private string _templateName = "";
        private string _templateMemo = "";
        private string _vlaSavePath = "";
        private bool _isExporting = false;
        private string _exportProgress = "";

        [MenuItem("VLA/Template Creator")]
        public static void ShowWindow()
        {
            var win = GetWindow<VLATemplateCreatorWindow>("VLA Template Creator");
            win.minSize = new Vector2(420, 340);
            win.maxSize = new Vector2(600, 500);
            win.Show();
        }

        private void OnEnable()
        {
            // VLABridgeWindow が既に開いている場合のみ保存先パスを取得（開いていなければデフォルト）
            if (EditorWindow.HasOpenInstances<VLABridgeWindow>())
            {
                var bridge = EditorWindow.GetWindow<VLABridgeWindow>(false, null, false);
                if (bridge != null)
                    _vlaSavePath = bridge.GetVlaSavePath();
            }
        }

        private void OnGUI()
        {
            if (_isExporting)
            {
                GUILayout.FlexibleSpace();
                GUILayout.Label("📦 テンプレート作成中…", new GUIStyle(EditorStyles.boldLabel) { alignment = TextAnchor.MiddleCenter, fontSize = 14 });
                GUILayout.Space(8);
                GUILayout.Label(_templateName, new GUIStyle(EditorStyles.label) { alignment = TextAnchor.MiddleCenter, fontSize = 12 });
                GUILayout.Space(4);
                GUILayout.Label(_exportProgress, new GUIStyle(EditorStyles.wordWrappedMiniLabel) { alignment = TextAnchor.MiddleCenter });
                GUILayout.FlexibleSpace();
                return;
            }

            EditorGUILayout.Space(12);

            // ── タイトル ──
            GUILayout.Label("📋 テンプレート作成", new GUIStyle(EditorStyles.boldLabel) { fontSize = 16, alignment = TextAnchor.MiddleCenter });
            GUILayout.Space(8);
            EditorGUILayout.HelpBox(
                "現在の Unity プロジェクトをテンプレートとして保存します。\n\n" +
                "含まれるもの:\n" +
                "・Assets 内の全ファイル（マテリアル・スクリプト・シーン等）\n" +
                "・Packages/manifest.json（UPM パッケージ情報）\n" +
                "・ProjectSettings（レンダリング・タグ・レイヤー設定等）\n" +
                "・360° パノラマ画像",
                MessageType.Info);
            GUILayout.Space(12);

            // ── テンプレート名 ──
            EditorGUILayout.LabelField("テンプレート名 *", EditorStyles.boldLabel);
            _templateName = EditorGUILayout.TextField(_templateName);
            if (string.IsNullOrWhiteSpace(_templateName))
            {
                EditorGUILayout.HelpBox("テンプレート名を入力してください。", MessageType.Warning);
            }
            GUILayout.Space(8);

            // ── メモ ──
            EditorGUILayout.LabelField("メモ（任意）", EditorStyles.boldLabel);
            _templateMemo = EditorGUILayout.TextArea(_templateMemo, GUILayout.Height(50));
            GUILayout.Space(8);

            // ── シーン情報 ──
            var scene = UnityEngine.SceneManagement.SceneManager.GetActiveScene();
            EditorGUILayout.LabelField($"シーン: {scene.name}  |  Unity: {Application.unityVersion}",
                new GUIStyle(EditorStyles.miniLabel) { normal = { textColor = new Color(0.6f, 0.6f, 0.7f) } });
            GUILayout.Space(12);

            // ── 保存先 ──
            string outputDir = GetTemplateOutputDir();
            EditorGUILayout.LabelField("保存先:", EditorStyles.miniLabel);
            EditorGUILayout.SelectableLabel(outputDir, EditorStyles.textField, GUILayout.Height(18));
            GUILayout.Space(16);

            // ── エクスポートボタン ──
            GUI.enabled = !string.IsNullOrWhiteSpace(_templateName);
            GUI.backgroundColor = new Color(0.25f, 0.65f, 0.85f);
            if (GUILayout.Button("📦 テンプレートを作成", GUILayout.Height(36)))
            {
                if (EditorUtility.DisplayDialog("テンプレート作成確認",
                    $"テンプレート「{_templateName}」を作成します。\n\n処理に数分かかる場合があります。続行しますか？",
                    "作成する", "キャンセル"))
                {
                    StartExport();
                }
            }
            GUI.backgroundColor = Color.white;
            GUI.enabled = true;
        }

        private string GetTemplateOutputDir()
        {
            string basePath = _vlaSavePath;
            if (string.IsNullOrEmpty(basePath))
                basePath = System.IO.Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.UserProfile), "VRC-Library");
            return System.IO.Path.Combine(basePath, "_VLA_templates");
        }

        private void StartExport()
        {
            _isExporting = true;
            _exportProgress = "準備中…";
            Repaint();

            // VLABridgeWindow が開いている場合のみ ExportTemplate を呼び出す
            if (!EditorWindow.HasOpenInstances<VLABridgeWindow>())
            {
                _isExporting = false;
                EditorUtility.DisplayDialog("エラー",
                    "VLA Bridge ウィンドウが開いていません。\n先に VLA > Library Bridge を開いてください。", "OK");
                return;
            }

            var bridge = EditorWindow.GetWindow<VLABridgeWindow>(false, null, false);
            if (bridge != null)
            {
                bridge.RequestTemplateExport(_templateName, _templateMemo, (success, error) =>
                {
                    _isExporting = false;
                    _exportProgress = "";
                    Repaint();

                    if (success)
                    {
                        EditorUtility.DisplayDialog("テンプレート作成完了",
                            $"テンプレート「{_templateName}」を作成しました。\n\nVLA の Template タブで確認できます。",
                            "OK");
                        _templateName = "";
                        _templateMemo = "";
                    }
                    else
                    {
                        EditorUtility.DisplayDialog("テンプレート作成失敗",
                            $"エラーが発生しました:\n{error}", "OK");
                    }
                });
            }
            else
            {
                _isExporting = false;
                EditorUtility.DisplayDialog("エラー", "VLA Bridge ウィンドウが見つかりません。\n先に VLA > Library Bridge を開いてください。", "OK");
            }
        }
    }

    // ═══════════════════════════════════════════════════════════
    // VLA Import Template — テンプレートインポートウィンドウ
    // VLA > Import Template メニューから開く。
    // VLA に保存されたテンプレートを選択して .unitypackage をインポートする。
    // ═══════════════════════════════════════════════════════════
    public class VLATemplateImportWindow : EditorWindow
    {
        private List<VLATemplateItem> _templates   = new List<VLATemplateItem>();
        private bool   _isLoading       = false;
        private bool   _hasLoaded       = false;
        private Vector2 _scroll;
        private string _statusMessage   = "";
        private string _importingId     = "";
        private string _pendingItemId   = "";
        private string _pendingPkgName  = "";

        [MenuItem("VLA/Import Template")]
        public static void ShowWindow()
        {
            var win = GetWindow<VLATemplateImportWindow>("VLA: Import Template");
            win.minSize = new Vector2(400, 340);
            win.Show();
            // 開いたときに Bridge が接続済みならすぐリスト取得
            win.TryFetchFromBridge();
        }

        private void OnEnable()
        {
            // Bridge がキャッシュ済みリストを持っていれば即時反映
            if (EditorWindow.HasOpenInstances<VLABridgeWindow>())
            {
                var bridge = EditorWindow.GetWindow<VLABridgeWindow>(false, null, false);
                if (bridge != null && bridge._templateListReceived)
                {
                    _templates = new List<VLATemplateItem>(bridge._cachedTemplateList);
                    _hasLoaded = true;
                }
            }
        }

        private void TryFetchFromBridge()
        {
            if (!EditorWindow.HasOpenInstances<VLABridgeWindow>())
            {
                _statusMessage = "VLA Bridge が開いていません。先に VLA > Library Bridge を開いてください。";
                return;
            }
            var bridge = EditorWindow.GetWindow<VLABridgeWindow>(false, null, false);
            if (bridge == null || bridge._connState != VLABridgeWindow.ConnectionState.Connected)
            {
                _statusMessage = "VLA.exe が起動していません。起動後に再試行してください。";
                return;
            }
            if (bridge._templateListReceived)
            {
                _templates = new List<VLATemplateItem>(bridge._cachedTemplateList);
                _hasLoaded = true;
                Repaint();
                return;
            }
            _isLoading     = true;
            _statusMessage = "";
            bridge.SendTemplateListRequest();
            Repaint();
        }

        // Bridge から呼ばれるコールバック
        public void OnTemplateListReceived(List<VLATemplateItem> templates)
        {
            _templates  = templates ?? new List<VLATemplateItem>();
            _isLoading  = false;
            _hasLoaded  = true;
            Repaint();
        }

        public void OnPackageFileReady(string itemId, string packageName, string filePath)
        {
            if (!File.Exists(filePath))
            {
                _statusMessage  = $"❌ パッケージファイルが見つかりません: {filePath}";
                _importingId    = "";
                SendImportResult(itemId, packageName, false, "file_not_found");
                Repaint();
                return;
            }

            EditorApplication.delayCall += () =>
            {
                try
                {
                    AssetDatabase.ImportPackage(filePath, true);
                    _pendingItemId  = itemId;
                    _pendingPkgName = packageName;
                    AssetDatabase.importPackageCompleted += OnCompleted;
                    AssetDatabase.importPackageCancelled += OnCancelled;
                    AssetDatabase.importPackageFailed    += OnFailed;
                }
                catch (Exception ex)
                {
                    _statusMessage = $"❌ インポートエラー: {ex.Message}";
                    _importingId   = "";
                    SendImportResult(itemId, packageName, false, ex.Message);
                    Repaint();
                }
            };

            void OnCompleted(string _)
            {
                UnregisterCallbacks();
                _statusMessage = $"✅ 「{packageName}」のインポートが完了しました";
                _importingId   = "";
                SendImportResult(_pendingItemId, _pendingPkgName, true, "");
                Repaint();
            }
            void OnCancelled(string _)
            {
                UnregisterCallbacks();
                _importingId = "";
                SendImportResult(_pendingItemId, _pendingPkgName, false, "cancelled");
                Repaint();
            }
            void OnFailed(string _, string err)
            {
                UnregisterCallbacks();
                _statusMessage = $"❌ インポート失敗: {err}";
                _importingId   = "";
                SendImportResult(_pendingItemId, _pendingPkgName, false, err);
                Repaint();
            }
            void UnregisterCallbacks()
            {
                AssetDatabase.importPackageCompleted -= OnCompleted;
                AssetDatabase.importPackageCancelled -= OnCancelled;
                AssetDatabase.importPackageFailed    -= OnFailed;
            }
        }

        private void SendImportResult(string itemId, string pkg, bool success, string error)
        {
            if (!EditorWindow.HasOpenInstances<VLABridgeWindow>()) return;
            EditorWindow.GetWindow<VLABridgeWindow>(false, null, false)
                ?.SendTemplateImportResult(itemId, pkg, success, error);
        }

        private void OnGUI()
        {
            // ── ツールバー ──
            EditorGUILayout.BeginHorizontal(EditorStyles.toolbar);
            GUILayout.Label("⬇  テンプレートをインポート", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            if (GUILayout.Button("🔄 更新", EditorStyles.toolbarButton, GUILayout.Width(60)))
            {
                _hasLoaded = false;
                TryFetchFromBridge();
            }
            EditorGUILayout.EndHorizontal();

            // ステータス
            if (!string.IsNullOrEmpty(_statusMessage))
            {
                EditorGUILayout.HelpBox(_statusMessage, MessageType.None);
                GUILayout.Space(4);
            }

            // 接続確認
            bool connected = false;
            if (EditorWindow.HasOpenInstances<VLABridgeWindow>())
            {
                var b = EditorWindow.GetWindow<VLABridgeWindow>(false, null, false);
                connected = b?._connState == VLABridgeWindow.ConnectionState.Connected;
            }
            if (!connected)
            {
                EditorGUILayout.HelpBox("VLA.exe が起動していません。起動後に再試行してください。", MessageType.Warning);
                if (GUILayout.Button("再試行", GUILayout.Height(28))) TryFetchFromBridge();
                return;
            }

            if (_isLoading)
            {
                EditorGUILayout.HelpBox("テンプレート一覧を取得中…", MessageType.None);
                return;
            }
            if (!_hasLoaded)
            {
                GUILayout.FlexibleSpace();
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("テンプレート一覧を取得", GUILayout.Width(200), GUILayout.Height(32)))
                    TryFetchFromBridge();
                GUILayout.FlexibleSpace();
                EditorGUILayout.EndHorizontal();
                GUILayout.FlexibleSpace();
                return;
            }
            if (_templates.Count == 0)
            {
                EditorGUILayout.HelpBox(
                    "インポートできるテンプレートがありません。\nVLA でテンプレートを作成すると表示されます。",
                    MessageType.Info);
                return;
            }

            EditorGUILayout.HelpBox("インポートボタンを押すと .unitypackage がこのプロジェクトに適用されます。", MessageType.Info);
            GUILayout.Space(4);

            _scroll = EditorGUILayout.BeginScrollView(_scroll);
            foreach (var tmpl in _templates)
                DrawCard(tmpl);
            EditorGUILayout.EndScrollView();
        }

        private void DrawCard(VLATemplateItem tmpl)
        {
            bool isImporting = _importingId == tmpl.template_id;

            EditorGUILayout.BeginVertical(EditorStyles.helpBox);
            EditorGUILayout.BeginHorizontal();

            GUILayout.Label("📋", new GUIStyle(EditorStyles.label) { fontSize = 26 },
                GUILayout.Width(38), GUILayout.Height(38));

            EditorGUILayout.BeginVertical();
            GUILayout.Label(tmpl.template_name ?? "(無名)", EditorStyles.boldLabel);

            var sb = new System.Text.StringBuilder();
            if (!string.IsNullOrEmpty(tmpl.unity_version))    sb.Append($"Unity {tmpl.unity_version}  ");
            if (!string.IsNullOrEmpty(tmpl.scene_name))       sb.Append($"Scene: {tmpl.scene_name}  ");
            if (!string.IsNullOrEmpty(tmpl.size_display))     sb.Append($"📦 {tmpl.size_display}  ");
            if (!string.IsNullOrEmpty(tmpl.created_at_short)) sb.Append($"🕒 {tmpl.created_at_short}");
            if (sb.Length > 0)
                GUILayout.Label(sb.ToString(), EditorStyles.miniLabel);
            if (!string.IsNullOrEmpty(tmpl.memo))
                GUILayout.Label(tmpl.memo, new GUIStyle(EditorStyles.wordWrappedMiniLabel)
                    { normal = { textColor = new Color(0.6f, 0.6f, 0.7f) } });
            EditorGUILayout.EndVertical();

            GUI.enabled = !isImporting && !string.IsNullOrEmpty(tmpl.template_id);
            GUI.backgroundColor = isImporting
                ? Color.gray
                : new Color(0.2f, 0.65f, 0.3f);  // 緑
            if (GUILayout.Button(isImporting ? "処理中…" : "⬇ Import",
                GUILayout.Height(30), GUILayout.Width(90)))
            {
                RequestImport(tmpl);
            }
            GUI.backgroundColor = Color.white;
            GUI.enabled = true;

            EditorGUILayout.EndHorizontal();
            EditorGUILayout.EndVertical();
            GUILayout.Space(2);
        }

        private void RequestImport(VLATemplateItem tmpl)
        {
            if (!EditorWindow.HasOpenInstances<VLABridgeWindow>()) return;
            var bridge = EditorWindow.GetWindow<VLABridgeWindow>(false, null, false);
            if (bridge == null) return;
            _importingId   = tmpl.template_id;
            _statusMessage = $"「{tmpl.template_name}」のパッケージを取得中…";
            Repaint();
            bridge.SendTemplateImportRequest(tmpl.template_id);
        }
    }

    // ── テンプレートアイテム型（Bridge ⇔ ImportWindow 共通） ──
    [Serializable]
    public class VLATemplateItem
    {
        public string template_id;
        public string template_name;
        public string memo;
        public string created_at_short;
        public string unity_version;
        public string scene_name;
        public string size_display;
        public string package_path;
    }

    [Serializable]
    public class TemplateListResponse
    {
        public string type;
        public List<VLATemplateItem> templates;
    }

    public class VLABridgeSettings : EditorWindow
    {
        private string _url; private Action<string> _onSave;
        public static void ShowWindow(string currentUrl, Action<string> onSave) { var w = GetWindow<VLABridgeSettings>("VLA Bridge Settings"); w._url = currentUrl; w._onSave = onSave; w.minSize = new Vector2(350, 120); w.maxSize = new Vector2(500, 120); w.Show(); }
        private void OnGUI() { EditorGUILayout.Space(8); EditorGUILayout.LabelField("VLA WebSocket URL", EditorStyles.boldLabel); _url = EditorGUILayout.TextField(_url); EditorGUILayout.Space(8); EditorGUILayout.BeginHorizontal(); GUILayout.FlexibleSpace(); if (GUILayout.Button("リセット", GUILayout.Width(80))) _url = "ws://localhost:27420/vla/"; if (GUILayout.Button("保存して再接続", GUILayout.Width(120))) { _onSave?.Invoke(_url); Close(); } EditorGUILayout.EndHorizontal(); }
    }
}
