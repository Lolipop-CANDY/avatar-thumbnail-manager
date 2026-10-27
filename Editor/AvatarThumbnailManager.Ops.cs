#if UNITY_EDITOR
// ============================================================================
//  データ操作 / セッション / 撮影処理
// ============================================================================
using System.Collections.Generic;
using System.IO;
using System.Linq;
using UnityEditor;
using UnityEditor.SceneManagement;
using UnityEngine;
using UnityEngine.SceneManagement;
using Object = UnityEngine.Object;

namespace LollipopCandy.AvatarHub
{
    public partial class AvatarThumbnailManager
    {
        private bool _batchBusy;
        private bool _viewDirty = true;

        // 個別カメラ読み込み中は、共通カメラ値を退避しておく
        private bool _camFromLook;
        private Vector3 _gPos, _gRot;
        private bool _gPan;
        private int _gFraming;

        private sealed class CaptureResult
        {
            public int ok, skipped, failed;
            public bool cancelled;
            public readonly List<string> notes = new List<string>();
        }

        // ---------------------------------------------------------------- データ読み込み

        private static List<GameObject> FindSceneAvatars(Scene scene)
        {
            var result = new List<GameObject>();
            if (!scene.IsValid() || !scene.isLoaded) return result;
            foreach (GameObject root in scene.GetRootGameObjects()) CollectAvatars(root.transform, result);
            return result;
        }

        private static void CollectAvatars(Transform t, List<GameObject> result)
        {
            if (AvatarIdentity.IsAvatar(t.gameObject))
            {
                result.Add(t.gameObject);
                return; // アバターの内側は探索しない
            }
            for (int i = 0; i < t.childCount; i++) CollectAvatars(t.GetChild(i), result);
        }

        private void ReloadForScene()
        {
            if (_saveEntriesAt >= 0 && !string.IsNullOrEmpty(_sceneKey)) SaveEntries();
            CloseSession();

            Scene scene = SceneManager.GetActiveScene();
            _sceneKey = ThumbStore.SceneKey(scene);
            _scenePath = scene.path ?? "";
            _entries.Clear();

            List<GameObject> avatars = FindSceneAvatars(scene);
            var claimed = new HashSet<GameObject>();

            foreach (AvatarRecord r in ThumbStore.Load(_sceneKey))
            {
                GameObject go = AvatarIdentity.ResolveStableId(r.id);
                if (go != null && !go.scene.IsValid()) go = null;
                if (go == null && !string.IsNullOrEmpty(r.blueprintId))
                {
                    go = avatars.FirstOrDefault(a => !claimed.Contains(a) && AvatarIdentity.GetBlueprintId(a) == r.blueprintId);
                }
                if (go == null && string.IsNullOrEmpty(r.id))
                {
                    go = avatars.FirstOrDefault(a => !claimed.Contains(a) && a.name == r.name); // 旧版データ互換
                }
                if (go != null)
                {
                    claimed.Add(go);
                    string sid = AvatarIdentity.GetStableId(go);
                    if (!string.IsNullOrEmpty(sid)) r.id = sid;
                    string bid = AvatarIdentity.GetBlueprintId(go);
                    if (!string.IsNullOrEmpty(bid)) r.blueprintId = bid;
                }

                _entries.Add(new AvatarEntry
                {
                    id = r.id,
                    blueprintId = r.blueprintId,
                    name = go != null ? go.name : r.name,
                    thumbnailPath = r.thumbnailPath,
                    memo = r.memo,
                    tag = r.tag ?? "",
                    manual = r.manual,
                    look = MigrateLook(r.look),
                    sceneObject = go
                });
            }

            AddNewAvatars(avatars);
            MarkDuplicates();
            RefreshThumbFlags();
            RefreshProjects();
            _viewDirty = true;
            if (_saveEntriesAt < 0 && _entries.Count > 0) MarkEntriesDirty();
            Repaint();
        }

        /// <summary>現在のアバター名に合わせた保存先(拡張子は今の画像と同じ)。他アバターとは重複させない。</summary>
        private string DesiredPath(AvatarEntry e)
        {
            var used = new HashSet<string>();
            foreach (AvatarEntry x in _entries)
            {
                if (x == e || string.IsNullOrEmpty(x.thumbnailPath)) continue;
                used.Add(ThumbStore.WithExtension(x.thumbnailPath, ".png"));
                used.Add(ThumbStore.WithExtension(x.thumbnailPath, ".jpg"));
            }
            string ext = string.IsNullOrEmpty(e.thumbnailPath) ? ".png" : Path.GetExtension(e.thumbnailPath).ToLowerInvariant();
            string p = ThumbStore.MakeThumbPath(_sceneKey, e.name, used);
            return ThumbStore.WithExtension(p, ext);
        }

        private void SyncNameFromScene(AvatarEntry e)
        {
            if (e.sceneObject != null && e.name != e.sceneObject.name) e.name = e.sceneObject.name;
            if (e.sceneObject != null)
            {
                // 再割り当てされたIDやBlueprint IDにも追従する
                string id = AvatarIdentity.GetStableId(e.sceneObject);
                if (!string.IsNullOrEmpty(id)) e.id = id;
                string bp = AvatarIdentity.GetBlueprintId(e.sceneObject);
                if (!string.IsNullOrEmpty(bp)) e.blueprintId = bp;
            }
        }

        /// <summary>画像ファイル名を現在のアバター名に合わせる(「リストを更新」時)。変更数を返す。</summary>
        private int SyncThumbNames()
        {
            int changed = 0;
            foreach (AvatarEntry e in _entries)
            {
                SyncNameFromScene(e);
                if (string.IsNullOrEmpty(e.thumbnailPath)) continue;
                string desired = DesiredPath(e);
                if (string.Equals(desired, e.thumbnailPath, System.StringComparison.OrdinalIgnoreCase)) continue;

                if (!File.Exists(e.thumbnailPath))
                {
                    e.thumbnailPath = desired; // 画像がまだ無ければ、次の撮影から新しい名前で保存
                    changed++;
                    continue;
                }
                if (File.Exists(desired)) continue; // 同名の別ファイルがあれば上書きしない

                Directory.CreateDirectory(Path.GetDirectoryName(desired));
                string err = AssetDatabase.MoveAsset(e.thumbnailPath, desired);
                if (!string.IsNullOrEmpty(err))
                {
                    Debug.LogWarning("[AvatarThumbnail] 画像の名前変更に失敗: " + err);
                    continue;
                }
                ThumbHistory.Rename(e.thumbnailPath, desired);
                e.thumbnailPath = desired;
                changed++;
            }
            if (changed > 0)
            {
                AssetDatabase.Refresh();
                RefreshThumbFlags();
            }
            return changed;
        }

        private static AvatarLook MigrateLook(AvatarLook look)
        {
            if (look == null) look = new AvatarLook();
            look.MigrateLegacy();
            return look;
        }

        /// <summary>シーン上のアバターのうち、リストに無いものを追加する。追加数を返す。</summary>
        private int AddNewAvatars(List<GameObject> avatars)
        {
            int added = 0;
            foreach (GameObject go in avatars)
            {
                if (_entries.Any(e => e.sceneObject == go)) continue;
                _entries.Add(NewEntry(go, false));
                added++;
            }
            return added;
        }

        private AvatarEntry NewEntry(GameObject go, bool manual)
        {
            var used = new HashSet<string>(_entries.Select(x => x.thumbnailPath));
            return new AvatarEntry
            {
                id = AvatarIdentity.GetStableId(go) ?? "",
                blueprintId = AvatarIdentity.GetBlueprintId(go) ?? "",
                name = go.name,
                thumbnailPath = ThumbStore.MakeThumbPath(_sceneKey, go.name, used),
                memo = "",
                manual = manual,
                sceneObject = go
            };
        }

        /// <summary>「リストを更新」。上書きONならシーンの状態でリストを作り直す(個別設定は引き継ぐ)。</summary>
        private void UpdateList()
        {
            Scene scene = SceneManager.GetActiveScene();
            List<GameObject> avatars = FindSceneAvatars(scene);

            if (s.overwriteList)
            {
                List<AvatarEntry> keep = _entries.Where(e => e.manual && e.sceneObject != null).ToList();
                var old = _entries.ToList();
                _entries.Clear();
                foreach (GameObject go in avatars)
                {
                    AvatarEntry prev = old.FirstOrDefault(e => e.sceneObject == go);
                    if (prev != null) _entries.Add(prev);
                    else _entries.Add(NewEntry(go, false));
                }
                foreach (AvatarEntry m in keep)
                {
                    if (!_entries.Contains(m)) _entries.Add(m);
                }
            }
            else
            {
                // シーンから消えたものも履歴として残し、新規だけ追加する
                foreach (AvatarEntry e in _entries)
                {
                    if (e.sceneObject == null && !string.IsNullOrEmpty(e.id))
                    {
                        GameObject go = AvatarIdentity.ResolveStableId(e.id);
                        if (go != null && go.scene.IsValid()) e.sceneObject = go;
                    }
                }
                AddNewAvatars(avatars);
            }

            MarkDuplicates();
            RefreshThumbFlags();
            int renamed = SyncThumbNames();
            MarkDuplicates();
            _viewDirty = true;
            MarkEntriesDirty();
            if (renamed > 0) ShowNotification(new GUIContent("リストを更新しました(画像名を " + renamed + " 件、現在のアバター名に合わせました)"));
            else ShowNotification(new GUIContent("リストを更新しました"));
        }

        private void AddManual(GameObject go)
        {
            if (go == null) return;
            if (!go.scene.IsValid())
            {
                ShowNotification(new GUIContent("シーン上のオブジェクトのみ追加できます"));
                return;
            }
            AvatarEntry ex = _entries.FirstOrDefault(e => e.sceneObject == go);
            if (ex != null)
            {
                ShowNotification(new GUIContent("「" + go.name + "」は既にリストにあります"));
                return;
            }
            _entries.Add(NewEntry(go, true)); // 条件チェックはしない(手動追加)
            MarkDuplicates();
            RefreshThumbFlags();
            _viewDirty = true;
            MarkEntriesDirty();
        }

        private void RemoveEntry(AvatarEntry e)
        {
            if (!EditorUtility.DisplayDialog("確認",
                    "「" + e.name + "」をリストから削除しますか？\n(撮影済みの画像ファイルは削除されません)", "削除", "キャンセル")) return;
            if (_previewEntry == e) CloseSession();
            _entries.Remove(e);
            if (e.thumbTex != null) DestroyImmediate(e.thumbTex);
            MarkDuplicates();
            _viewDirty = true;
            MarkEntriesDirty();
        }

        private void MoveEntry(int from, int to)
        {
            if (from < 0 || from >= _entries.Count) return;
            to = Mathf.Clamp(to, 0, _entries.Count - 1);
            if (from == to) return;
            AvatarEntry e = _entries[from];
            _entries.RemoveAt(from);
            _entries.Insert(to, e);
            _viewDirty = true;
            MarkEntriesDirty();
        }

        private void MarkDuplicates()
        {
            foreach (AvatarEntry e in _entries)
            {
                AvatarEntry me = e;
                e.duplicateName = _entries.Count(x => x != me && x.name == me.name) > 0;
            }
        }

        private void SaveEntries()
        {
            _saveEntriesAt = -1;
            if (string.IsNullOrEmpty(_sceneKey)) return;
            ThumbStore.Save(_sceneKey, _scenePath, _entries);
        }

        private void RefreshThumbFlags()
        {
            foreach (AvatarEntry e in _entries)
            {
                string p = e.thumbnailPath;
                bool exists = !string.IsNullOrEmpty(p) && File.Exists(p);
                if (!exists && !string.IsNullOrEmpty(p))
                {
                    // 出力形式を変えた後などで拡張子だけ違うファイルを拾う
                    string alt = ThumbStore.WithExtension(p, Path.GetExtension(p).ToLowerInvariant() == ".png" ? ".jpg" : ".png");
                    if (File.Exists(alt))
                    {
                        e.thumbnailPath = alt;
                        exists = true;
                    }
                }
                System.DateTime t = exists ? File.GetLastWriteTime(e.thumbnailPath) : System.DateTime.MinValue;
                if (t != e.thumbTime && e.thumbTex != null)
                {
                    DestroyImmediate(e.thumbTex);
                    e.thumbTex = null;
                }
                e.thumbTime = t;
                e.hasThumb = exists;
            }
        }

        private void RefreshProjects()
        {
            _projects = ThumbStore.ReadAll().projects;
            _sharedThumbs.Clear();
            foreach (ProjectRecord p in _projects)
            {
                if (p.projectName == _sceneKey || p.avatars == null) continue;
                foreach (AvatarRecord a in p.avatars)
                {
                    if (string.IsNullOrEmpty(a.blueprintId) || string.IsNullOrEmpty(a.thumbnailPath)) continue;
                    if (_sharedThumbs.ContainsKey(a.blueprintId)) continue;
                    string path = a.thumbnailPath;
                    if (!File.Exists(path))
                    {
                        path = ThumbStore.WithExtension(path, Path.GetExtension(path).ToLowerInvariant() == ".png" ? ".jpg" : ".png");
                        if (!File.Exists(path)) continue;
                    }
                    _sharedThumbs[a.blueprintId] = path;
                }
            }
        }

        private string SharedFor(AvatarEntry e)
        {
            string p;
            if (!string.IsNullOrEmpty(e.blueprintId) && _sharedThumbs.TryGetValue(e.blueprintId, out p)) return p;
            return null;
        }

        private void ReuseShared(AvatarEntry e)
        {
            string src = SharedFor(e);
            if (src == null) return;
            if (e.hasThumb && !EditorUtility.DisplayDialog("確認", "既存の画像を、他シーンの画像で置き換えますか？", "置き換える", "キャンセル")) return;
            if (e.hasThumb) ThumbHistory.Archive(e.thumbnailPath);
            string dst = ThumbStore.WithExtension(e.thumbnailPath, Path.GetExtension(src).ToLowerInvariant());
            Directory.CreateDirectory(Path.GetDirectoryName(dst));
            File.Copy(src, dst, true);
            e.thumbnailPath = dst;
            AssetDatabase.ImportAsset(dst);
            RefreshThumbFlags();
            _viewDirty = true;
            MarkEntriesDirty();
        }

        /// <summary>リスト表示用の縮小サムネイル(最大160px)。</summary>
        private static Texture2D LoadSmallThumb(string path)
        {
            Texture2D src = null;
            try
            {
                src = new Texture2D(2, 2, TextureFormat.RGBA32, false);
                if (!src.LoadImage(File.ReadAllBytes(path))) return null;
                int max = 160;
                if (src.width <= max && src.height <= max)
                {
                    Texture2D copy = src;
                    src = null;
                    return copy;
                }
                float k = max / (float)Mathf.Max(src.width, src.height);
                int w = Mathf.Max(1, Mathf.RoundToInt(src.width * k));
                int h = Mathf.Max(1, Mathf.RoundToInt(src.height * k));
                Color32[] sp = src.GetPixels32();
                var dp = new Color32[w * h];
                for (int y = 0; y < h; y++)
                {
                    int sy = Mathf.Min(src.height - 1, (int)((y + 0.5f) / k));
                    for (int x = 0; x < w; x++)
                    {
                        int sx = Mathf.Min(src.width - 1, (int)((x + 0.5f) / k));
                        dp[y * w + x] = sp[sy * src.width + sx];
                    }
                }
                var dst = new Texture2D(w, h, TextureFormat.RGBA32, false);
                dst.SetPixels32(dp);
                dst.Apply(false);
                return dst;
            }
            catch (System.Exception)
            {
                return null;
            }
            finally
            {
                if (src != null) DestroyImmediate(src);
            }
        }

        // ---------------------------------------------------------------- プレビューセッション

        private void OpenSession(AvatarEntry e, bool loadCamera)
        {
            if (e == null || e.sceneObject == null) return;
            try
            {
                _session = RenderSession.Create(e.sceneObject, s);
            }
            catch (System.Exception ex)
            {
                _session = null;
                Debug.LogError("[AvatarThumbnail] プレビューの作成に失敗: " + ex);
                EditorUtility.DisplayDialog("プレビュー", "プレビューを作成できませんでした。\n" + ex.Message, "OK");
                return;
            }

            _previewEntry = e;
            _pickIndex = Mathf.Max(0, _entries.Where(x => !x.IsMissing).ToList().IndexOf(e));
            _session.ApplyLook(e.look);
            if (loadCamera) LoadCameraFor(e);
            else if (s.FramingValue != Framing.Custom && !_camFromLook) ApplyFramingNow();

            _tab = (int)TabId.Capture;
            _lastSig = int.MinValue;
            _previewDirty = true;
            _issuesDirty = true;
            _lastSync = EditorApplication.timeSinceStartup;
            Repaint();
        }

        private void CloseSession(bool keepCamera = false)
        {
            if (_session != null)
            {
                _session.Dispose();
                _session = null;
            }
            _previewEntry = null;
            if (!keepCamera) RestoreGlobalCamera();
            if (!keepCamera && _previewTex != null)
            {
                DestroyImmediate(_previewTex);
                _previewTex = null;
            }
            _issues = new List<CheckIssue>();
        }

        private void RestoreGlobalCamera()
        {
            if (!_camFromLook) return;
            _camFromLook = false;
            s.camPos = _gPos;
            s.camRot = _gRot;
            s.usePanYaw = _gPan;
            s.framing = _gFraming;
        }

        /// <summary>プレビューするアバターの個別カメラ(保存済みなら)を作業カメラへ反映する。</summary>
        private void LoadCameraFor(AvatarEntry e)
        {
            if (s.perAvatarCamera && e.look.hasCamera)
            {
                if (!_camFromLook)
                {
                    _gPos = s.camPos;
                    _gRot = s.camRot;
                    _gPan = s.usePanYaw;
                    _gFraming = s.framing;
                    _camFromLook = true;
                }
                s.camPos = e.look.camPos;
                s.camRot = e.look.camRot;
                s.usePanYaw = e.look.usePanYaw;
                s.framing = e.look.framing;
            }
            else
            {
                RestoreGlobalCamera();
                if (s.FramingValue != Framing.Custom) ApplyFramingNow();
            }
        }

        /// <summary>撮影に使うカメラ。個別保存 → 構図プリセット(アバターごとに距離自動) → 共通カメラの順。</summary>
        private CamRig RigFor(AvatarEntry e, RenderSession session)
        {
            if (s.perAvatarCamera && e.look.hasCamera)
            {
                return new CamRig(e.look.camPos, e.look.camRot, s.autoHeight);
            }
            Vector3 pos = _camFromLook ? _gPos : s.camPos;
            Vector3 rot = _camFromLook ? _gRot : s.camRot;
            int framing = _camFromLook ? _gFraming : s.framing;
            if ((Framing)framing != Framing.Custom)
            {
                return session.ComputeFraming((Framing)framing, s.autoHeight, s.Aspect);
            }
            return new CamRig(pos, rot, s.autoHeight);
        }

        // ---------------------------------------------------------------- 撮影

        private void CaptureSelected()
        {
            List<AvatarEntry> targets = _entries.Where(e => e.selected && !e.IsMissing).ToList();
            if (targets.Count == 0)
            {
                EditorUtility.DisplayDialog("エラー", "撮影対象のアバターがありません", "OK");
                return;
            }

            RefreshThumbFlags();
            int existing = s.skipExisting ? targets.Count(e => e.hasThumb) : 0;
            if (s.showRetakeWarning && existing > 0)
            {
                string message =
                    "撮影済のアバターが " + existing + " 体含まれるため、スキップされますがよろしいですか？\n\n" +
                    "※プレビュー表示と撮影画像はアバター表示が異なる場合がございます\n" +
                    "※この警告文は「再撮影警告文」オプションをオフで非表示にできます";
                if (!EditorUtility.DisplayDialog("確認", message, "撮影する", "キャンセル")) return;
            }

            CaptureResult r = RunCapture(targets, false);
            ShowSummary(r);
        }

        private void CaptureOne(AvatarEntry e)
        {
            RefreshThumbFlags();
            if (e.hasThumb && !EditorUtility.DisplayDialog("確認", e.name + " は既に撮影済みです。\n再撮影しますか?", "撮影する", "キャンセル"))
            {
                return;
            }
            CaptureResult r = RunCapture(new List<AvatarEntry> { e }, true);
            if (r.ok == 1 && e.hasThumb && s.compareAfterCapture && ThumbHistory.List(e.thumbnailPath).Count > 0)
            {
                OpenCompare(e, true);
            }
            else
            {
                ShowSummary(r);
            }
        }

        private void CaptureFromPreview()
        {
            if (_session == null || _previewEntry == null) return;
            AvatarEntry e = _previewEntry;
            bool had;
            try
            {
                SaveThumbnail(_session, e, out had);
                AssetDatabase.Refresh();
                _viewDirty = true;
                MarkEntriesDirty();
                if (had && s.compareAfterCapture) OpenCompare(e, true);
                else ShowNotification(new GUIContent(e.name + " を撮影しました"));
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[AvatarThumbnail] 撮影に失敗: " + ex);
                EditorUtility.DisplayDialog("撮影エラー", e.name + "\n" + ex.Message, "OK");
            }
        }

        private void OpenCompare(AvatarEntry e, bool afterCapture)
        {
            ThumbCompareWindow.Open(e.name, e.thumbnailPath, newPath =>
            {
                e.thumbnailPath = newPath;
                RefreshThumbFlags();
                _viewDirty = true;
                MarkEntriesDirty();
                Repaint();
            }, afterCapture);
        }

        private CaptureResult RunCapture(List<AvatarEntry> targets, bool force)
        {
            var result = new CaptureResult();
            try
            {
                for (int i = 0; i < targets.Count; i++)
                {
                    AvatarEntry e = targets[i];
                    if (EditorUtility.DisplayCancelableProgressBar("サムネイル撮影",
                            e.name + " (" + (i + 1) + "/" + targets.Count + ")", i / (float)targets.Count))
                    {
                        result.cancelled = true;
                        break;
                    }

                    if (e.IsMissing) { result.failed++; result.notes.Add(e.name + ": シーン上に見つかりません"); continue; }

                    if (!force && s.skipExisting && e.hasThumb)
                    {
                        result.skipped++;
                        continue;
                    }

                    if (!e.hasThumb && s.reuseSharedThumb && SharedFor(e) != null)
                    {
                        try
                        {
                            string src = SharedFor(e);
                            string dst = ThumbStore.WithExtension(e.thumbnailPath, Path.GetExtension(src).ToLowerInvariant());
                            Directory.CreateDirectory(Path.GetDirectoryName(dst));
                            File.Copy(src, dst, true);
                            e.thumbnailPath = dst;
                            e.hasThumb = true;
                            result.ok++;
                            result.notes.Add(e.name + ": 他シーンの画像を流用しました");
                            continue;
                        }
                        catch (System.Exception) { /* 通常撮影へ */ }
                    }

                    RenderSession session = null;
                    bool own = false;
                    try
                    {
                        if (_session != null && _previewEntry == e)
                        {
                            session = _session;
                        }
                        else
                        {
                            session = RenderSession.Create(e.sceneObject, s);
                            own = true;
                            session.ApplyLook(e.look);
                        }

                        foreach (CheckIssue c in session.RunChecks(RigFor(e, session), s.Aspect, s.FramingValue))
                        {
                            if (c.level >= 1) result.notes.Add(e.name + ": " + c.text);
                        }

                        bool had;
                        SaveThumbnail(session, e, out had);
                        result.ok++;
                    }
                    catch (System.Exception ex)
                    {
                        result.failed++;
                        result.notes.Add(e.name + ": 撮影に失敗 (" + ex.Message + ")");
                        Debug.LogError("[AvatarThumbnail] " + e.name + " の撮影に失敗: " + ex);
                    }
                    finally
                    {
                        if (own && session != null) session.Dispose();
                    }
                }
            }
            finally
            {
                EditorUtility.ClearProgressBar();
                AssetDatabase.Refresh();
                RefreshThumbFlags();
                _viewDirty = true;
                if (!_batchBusy) SaveEntries();
            }
            return result;
        }

        private void ShowSummary(CaptureResult r)
        {
            string msg = string.Format("撮影 {0} 体 / スキップ {1} 体 / 失敗 {2} 体{3}", r.ok, r.skipped, r.failed, r.cancelled ? "\n(途中でキャンセルしました)" : "");
            if (r.notes.Count > 0)
            {
                msg += "\n\n【確認してください】\n" + string.Join("\n", r.notes.Take(8).ToArray());
                if (r.notes.Count > 8) msg += "\n…ほか " + (r.notes.Count - 8) + " 件(Consoleを参照)";
                for (int i = 8; i < r.notes.Count; i++) Debug.LogWarning("[AvatarThumbnail] " + r.notes[i]);
            }
            msg += "\n\n※プレビュー表示と撮影画像はアバター表示が異なる場合がございます";
            EditorUtility.DisplayDialog("撮影完了", msg, "OK");
        }

        /// <summary>撮影して保存する。上書き前の画像は履歴へ退避する。</summary>
        private void SaveThumbnail(RenderSession session, AvatarEntry e, out bool archivedOld)
        {
            archivedOld = false;
            SyncNameFromScene(e);
            CamRig rig = RigFor(e, session);
            ResolvedBg bg = BgResolver.Resolve(EffectiveBg(e), session, s.Format, false);
            Texture2D tex = session.RenderComposite(s.outW, s.outH, rig, bg, null);
            if (tex == null) throw new System.Exception("画像を生成できませんでした");

            try
            {
                byte[] bytes = s.Format == OutFormat.Jpg ? tex.EncodeToJPG(s.jpgQuality) : tex.EncodeToPNG();
                string ext = ThumbSettings.Extension(s.Format);
                string oldPath = e.thumbnailPath;
                // 保存先は「現在のアバター名」で決める(改名後に撮影した画像も新しい名前になる)
                string newPath = ThumbStore.WithExtension(DesiredPath(e), ext);

                Directory.CreateDirectory(Path.GetDirectoryName(newPath));

                string existing = File.Exists(newPath) ? newPath : (File.Exists(oldPath) ? oldPath : null);
                if (existing != null) archivedOld = ThumbHistory.Archive(existing, newPath) != null;
                if (!string.Equals(oldPath, newPath, System.StringComparison.OrdinalIgnoreCase)) ThumbHistory.Rename(oldPath, newPath);

                File.WriteAllBytes(newPath, bytes);
                if (existing != null && existing != newPath && File.Exists(existing))
                {
                    AssetDatabase.DeleteAsset(existing); // 旧名・旧形式のファイル(履歴に退避済み)
                }
                e.thumbnailPath = newPath;
                e.hasThumb = true;
                e.thumbTime = File.GetLastWriteTime(newPath);
                if (e.thumbTex != null)
                {
                    DestroyImmediate(e.thumbTex);
                    e.thumbTex = null;
                }
                AssetDatabase.ImportAsset(newPath);
            }
            finally
            {
                DestroyImmediate(tex);
            }
        }

        private void OpenSaveFolder()
        {
            Directory.CreateDirectory(ThumbStore.LogFolder);
            AssetDatabase.Refresh();

            Object folder = AssetDatabase.LoadAssetAtPath<Object>(ThumbStore.LogFolder);
            if (folder == null) return;
            EditorUtility.FocusProjectWindow();
            Selection.activeObject = folder;
            EditorGUIUtility.PingObject(folder);
        }

        // ---------------------------------------------------------------- VRChat SDK アイコン

        private async void SetSdkIcon(AvatarEntry e)
        {
            if (e.sceneObject == null || !File.Exists(e.thumbnailPath)) return;

            string imagePath = SdkIconBridge.CreateSdkImage(e.thumbnailPath);
            if (imagePath == null)
            {
                EditorUtility.DisplayDialog("SDKアイコン", "SDK用の画像を作成できませんでした。", "OK");
                return;
            }

            string blueprint = AvatarIdentity.GetBlueprintId(e.sceneObject);
            string message =
                "「" + e.name + "」の撮影画像をVRChatのアバターアイコンに設定します。\n\n" +
                "・自動で設定: SDKのAPIでオンラインのアバターのアイコンを更新します(すぐ反映されます)\n" +
                "・手動で設定: SDKパネルを開き、画像の場所を開いてパスをコピーします\n\n" +
                "Blueprint ID: " + (string.IsNullOrEmpty(blueprint) ? "(未設定: 未アップロード)" : blueprint) + "\n" +
                "※アイコンは4:3で中央が切り出されます(プレビューのガイド参照)";

            int choice = EditorUtility.DisplayDialogComplex("SDKアイコンに設定", message, "自動で設定", "キャンセル", "手動で設定");
            if (choice == 1) return;
            if (choice == 2)
            {
                ManualSdkGuide(imagePath);
                return;
            }

            if (string.IsNullOrEmpty(blueprint))
            {
                EditorUtility.DisplayDialog("SDKアイコン",
                    "このアバターにはBlueprint IDがありません(未アップロード)。\n手動設定の手順を案内します。", "OK");
                ManualSdkGuide(imagePath);
                return;
            }

            SdkIconResult result;
            try
            {
                EditorUtility.DisplayProgressBar("SDKアイコン", "VRChatへ送信中...", 0.5f);
                result = await SdkIconBridge.TryUploadAsync(blueprint, imagePath);
            }
            finally
            {
                EditorUtility.ClearProgressBar();
            }

            if (result.ok)
            {
                EditorUtility.DisplayDialog("SDKアイコン", result.message, "OK");
                return;
            }

            Debug.LogWarning("[AvatarThumbnail] SDKアイコンの自動設定に失敗: " + result.message);
            EditorUtility.DisplayDialog("SDKアイコン",
                "自動設定に失敗しました。\n" + result.message + "\n\n手動設定の手順を案内します。", "OK");
            ManualSdkGuide(imagePath);
        }

        private static void ManualSdkGuide(string imagePath)
        {
            SdkIconBridge.PrepareManual(imagePath);
            EditorUtility.DisplayDialog("手動で設定",
                "SDKコントロールパネルを開き、画像の場所を開きました(パスはクリップボードにもコピー済み)。\n\n" +
                "Builder → 対象アバター → サムネイルの「Select Image」で、次のファイルを選んでください。\n" + imagePath,
                "OK");
        }

        // ---------------------------------------------------------------- 全シーン一括撮影

        private void RunBatchScenes(List<ProjectRecord> scenes)
        {
            if (scenes.Count == 0) return;

            // 既存画像の警告(従来と同じ文言)
            int existing = 0;
            if (s.skipExisting && s.showRetakeWarning)
            {
                foreach (ProjectRecord p in scenes)
                {
                    if (p.avatars == null) continue;
                    existing += p.avatars.Count(a => !string.IsNullOrEmpty(a.thumbnailPath) &&
                        (File.Exists(a.thumbnailPath) ||
                         File.Exists(ThumbStore.WithExtension(a.thumbnailPath, Path.GetExtension(a.thumbnailPath).ToLowerInvariant() == ".png" ? ".jpg" : ".png"))));
                }
            }
            string head = scenes.Count + " 個のシーンを順に開いて撮影します。\n(撮影後、元のシーンに戻ります)";
            if (existing > 0)
            {
                head += "\n\n撮影済のアバターが " + existing + " 体含まれるため、スキップされますがよろしいですか？\n\n" +
                        "※プレビュー表示と撮影画像はアバター表示が異なる場合がございます\n" +
                        "※この警告文は「再撮影警告文」オプションをオフで非表示にできます";
            }
            if (!EditorUtility.DisplayDialog("全シーン一括撮影", head, "撮影する", "キャンセル")) return;

            if (!EditorSceneManager.SaveCurrentModifiedScenesIfUserWantsTo()) return;

            string original = SceneManager.GetActiveScene().path;
            if (string.IsNullOrEmpty(original))
            {
                EditorUtility.DisplayDialog("全シーン一括撮影", "現在のシーンが未保存のため、撮影後に元へ戻せません。先にシーンを保存してください。", "OK");
                return;
            }

            _batchBusy = true;
            CloseSession();
            if (_saveEntriesAt >= 0) SaveEntries();

            var total = new CaptureResult();
            try
            {
                foreach (ProjectRecord p in scenes)
                {
                    if (string.IsNullOrEmpty(p.scenePath) || !File.Exists(p.scenePath))
                    {
                        total.failed++;
                        total.notes.Add(p.projectName + ": シーンファイルが見つかりません");
                        continue;
                    }
                    if (SceneManager.GetActiveScene().path != p.scenePath)
                    {
                        EditorSceneManager.OpenScene(p.scenePath, OpenSceneMode.Single);
                    }
                    ReloadForScene();
                    List<AvatarEntry> targets = _entries.Where(e => !e.IsMissing).ToList();
                    CaptureResult r = RunCapture(targets, false);
                    SaveEntries();
                    total.ok += r.ok;
                    total.skipped += r.skipped;
                    total.failed += r.failed;
                    foreach (string n in r.notes) total.notes.Add("[" + p.projectName + "] " + n);
                    if (r.cancelled)
                    {
                        total.cancelled = true;
                        break;
                    }
                }
            }
            catch (System.Exception ex)
            {
                Debug.LogError("[AvatarThumbnail] 一括撮影中にエラー: " + ex);
                total.notes.Add("エラー: " + ex.Message);
            }
            finally
            {
                if (SceneManager.GetActiveScene().path != original)
                {
                    EditorSceneManager.OpenScene(original, OpenSceneMode.Single);
                }
                _batchBusy = false;
                ReloadForScene();
            }
            ShowSummary(total);
        }
    }
}
#endif
