#if UNITY_EDITOR
using UnityEngine;
using UnityEditor;
using System.Collections.Generic;
using System.IO;
using System.Linq;

namespace LollipopCandy.AvatarHub
{
    /// <summary>
    /// アバターサムネイル撮影管理ツール
    /// シンプル方式：メインプロジェクト内で直接撮影
    /// </summary>
    public class AvatarThumbnailManager : EditorWindow
    {
        #region Constants
        
        private const int THUMBNAIL_SIZE = 1024;
        private const string LOG_FOLDER = "Assets/AvatarThumbnailManager/log";
        private const string DATABASE_FILE = "AvatarDatabase.json";
        
        // デフォルト値
        private static readonly Color DEFAULT_BACKGROUND_COLOR = new Color(0.2f, 0.2f, 0.2f, 1f);
        private static readonly Vector3 DEFAULT_CAMERA_POSITION = new Vector3(0, 0.1f, 1f); // アバター中心
        private static readonly Vector3 DEFAULT_CAMERA_ROTATION = new Vector3(0, 180f, 0);
        
        // UI サイズ
        private const float BUTTON_WIDTH_SMALL = 30f;
        private const float BUTTON_WIDTH_MEDIUM = 50f;
        private const float BUTTON_WIDTH_LARGE = 80f;
        private const float BUTTON_WIDTH_XLARGE = 150f;
        private const float AVATAR_NAME_WIDTH = 150f;
        private const float STATUS_WIDTH = 80f;
        private const int AVATAR_LIST_HEIGHT = 250;
        private const int PREVIEW_SIZE = 256;
        
        #endregion
        
        #region Fields
        
        private Color backgroundColor;
        private Vector3 cameraPosition;
        private Vector3 cameraRotation;
        private bool autoHeightAdjust = true;
        private bool skipExistingThumbnails = true;
        private bool overwriteAvatarList = false;
        private bool showPreview = false;
        private bool usePanYaw = false; // false=軸、true=回転
        private bool showRetakeWarning = true; // 再撮影警告文の表示
        
        private List<AvatarInfo> avatarList = new List<AvatarInfo>();
        private Vector2 scrollPosition;
        private Texture2D previewTexture;
        private int selectedPreviewIndex = -1;
        
        private Camera previewCamera;
        private RenderTexture previewRT;
        private GameObject previewAvatarInstance;
        
        // 撮影用一時オブジェクト
        private Camera captureCamera;
        private Light captureLight;
        
        #endregion
        
        #region Menu Items
        
        [UnityEditor.MenuItem("ろりぽっぷCANDY/あばたーはぶリスト作成ツール")]
        public static void ShowWindow()
        {
            var window = GetWindow<AvatarThumbnailManager>("あばたーはぶリスト作成");
            window.minSize = new Vector2(450, 700);
            window.Show();
        }
        
        #endregion
        
        #region Unity Lifecycle
        
        private void OnEnable()
        {
            InitializeDefaultValues();
            LoadAvatarList();
            RefreshAvatarList();
        }
        
        private void OnDisable()
        {
            CleanupPreview();
            CleanupCaptureObjects();
        }
        
        private void OnDestroy()
        {
            CleanupPreview();
            CleanupCaptureObjects();
        }
        
        /// <summary>
        /// デフォルト値を初期化
        /// </summary>
        private void InitializeDefaultValues()
        {
            if (backgroundColor == Color.clear)
            {
                backgroundColor = DEFAULT_BACKGROUND_COLOR;
            }
            if (cameraPosition == Vector3.zero)
            {
                cameraPosition = DEFAULT_CAMERA_POSITION;
            }
            if (cameraRotation == Vector3.zero)
            {
                cameraRotation = DEFAULT_CAMERA_ROTATION;
            }
        }
        
        #endregion
        
        #region GUI
        
        private void OnGUI()
        {
            EditorGUILayout.Space(10);
            EditorGUILayout.LabelField("アバターサムネイル撮影ツール", EditorStyles.boldLabel);
            EditorGUILayout.Space(5);
            
            DrawCameraSettings();
            EditorGUILayout.Space(10);
            DrawOptions();
            
            // 非表示アバターの警告（プレビューの上）
            EditorGUILayout.HelpBox("※ 非表示のアバターはプレビュー時に透明になります", MessageType.Info);
            EditorGUILayout.Space(5);
            
            // プレビュー表示
            if (showPreview)
            {
                DrawPreview();
                EditorGUILayout.Space(10);
            }
            
            DrawAvatarList();
            EditorGUILayout.Space(10);
            DrawActionButtons();
        }
        
        private void DrawCameraSettings()
        {
            EditorGUILayout.LabelField("カメラ設定", EditorStyles.boldLabel);
            
            backgroundColor = EditorGUILayout.ColorField("Background Color", backgroundColor);
            
            // 軸/回転切り替え
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Toggle(!usePanYaw, "軸", EditorStyles.miniButtonLeft, GUILayout.Width(60)))
            {
                usePanYaw = false;
            }
            if (GUILayout.Toggle(usePanYaw, "回転", EditorStyles.miniButtonRight, GUILayout.Width(60)))
            {
                usePanYaw = true;
            }
            GUILayout.FlexibleSpace();
            EditorGUILayout.EndHorizontal();
            
            EditorGUI.indentLevel++;
            EditorGUI.BeginChangeCheck();
            
            if (!usePanYaw)
            {
                // 軸移動モード
                EditorGUILayout.LabelField("カメラ位置（軸移動）");
                
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("X (左右)", GUILayout.Width(100));
                EditorGUILayout.LabelField("X (左右)", GUILayout.Width(100));
                float newX = EditorGUILayout.Slider(cameraPosition.x, -5f, 5f);
                if (newX != cameraPosition.x)
                {
                    Undo.RecordObject(this, "カメラ位置変更");
                    cameraPosition.x = newX;
                }
                EditorGUILayout.EndHorizontal();
                
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Y (上下)", GUILayout.Width(100));
                EditorGUILayout.LabelField("Y (上下)", GUILayout.Width(100));
                float newY = EditorGUILayout.Slider(cameraPosition.y, -5f, 5f);
                if (newY != cameraPosition.y)
                {
                    Undo.RecordObject(this, "カメラ位置変更");
                    cameraPosition.y = newY;
                }
                EditorGUILayout.EndHorizontal();
                
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("Z (前後)", GUILayout.Width(100));
                EditorGUILayout.LabelField("Z (前後)", GUILayout.Width(100));
                float newZ = EditorGUILayout.Slider(cameraPosition.z, -10f, 10f);
                if (newZ != cameraPosition.z)
                {
                    Undo.RecordObject(this, "カメラ位置変更");
                    cameraPosition.z = newZ;
                }
                if (GUILayout.Button("正面", GUILayout.Width(50)))
                {
                    Undo.RecordObject(this, "カメラ位置変更");
                    cameraPosition = new Vector3(0, 0, 2f);
                    cameraRotation.y = 180f;
                }
                if (GUILayout.Button("背面", GUILayout.Width(50)))
                {
                    Undo.RecordObject(this, "カメラ位置変更");
                    cameraPosition = new Vector3(0, 0, -2f);
                    cameraRotation.y = 0f;
                }
                EditorGUILayout.EndHorizontal();
                
                // 初期化ボタン
                if (GUILayout.Button("カメラを初期設定に戻す"))
                {
                    Undo.RecordObject(this, "カメラ初期化");
                    ResetCameraSettings();
                    if (showPreview)
                    {
                        UpdatePreview();
                    }
                }
            }
            else
            {
                // 回転モード
                EditorGUILayout.LabelField("カメラ角度（回転）");
                
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("パン (水平)", GUILayout.Width(100));
                EditorGUILayout.LabelField("パン (水平)", GUILayout.Width(100));
                float newPan = EditorGUILayout.Slider(cameraRotation.y, 0f, 360f);
                if (newPan != cameraRotation.y)
                {
                    Undo.RecordObject(this, "カメラ回転変更");
                    cameraRotation.y = newPan;
                }
                EditorGUILayout.EndHorizontal();
                
                EditorGUILayout.BeginHorizontal();
                EditorGUILayout.LabelField("ヨー (垂直)", GUILayout.Width(100));
                EditorGUILayout.LabelField("ヨー (垂直)", GUILayout.Width(100));
                float newYaw = EditorGUILayout.Slider(cameraRotation.x, -89f, 89f);
                if (newYaw != cameraRotation.x)
                {
                    Undo.RecordObject(this, "カメラ回転変更");
                    cameraRotation.x = newYaw;
                }
                EditorGUILayout.EndHorizontal();
                
                EditorGUILayout.BeginHorizontal();
                GUILayout.FlexibleSpace();
                if (GUILayout.Button("正面", GUILayout.Width(50)))
                {
                    Undo.RecordObject(this, "カメラ回転変更");
                    cameraRotation.y = 180f;
                    cameraRotation.x = 0f;
                }
                if (GUILayout.Button("背面", GUILayout.Width(50)))
                {
                    Undo.RecordObject(this, "カメラ回転変更");
                    cameraRotation.y = 0f;
                    cameraRotation.x = 0f;
                }
                EditorGUILayout.EndHorizontal();
                
                // 初期化ボタン
                if (GUILayout.Button("カメラを初期設定に戻す"))
                {
                    Undo.RecordObject(this, "カメラ初期化");
                    ResetCameraSettings();
                    if (showPreview)
                    {
                        UpdatePreview();
                    }
                }
            }
            
            if (EditorGUI.EndChangeCheck() && showPreview)
            {
                UpdatePreview();
            }
            
            EditorGUI.indentLevel--;
        }
        
        private void ResetCameraSettings()
        {
            cameraPosition = DEFAULT_CAMERA_POSITION;
            cameraRotation = DEFAULT_CAMERA_ROTATION;
            backgroundColor = DEFAULT_BACKGROUND_COLOR;
            usePanYaw = false;
        }
        
/// <summary>
        /// 保存フォルダを開く（Unity Projectタブ内）
        /// </summary>
        private void OpenSaveFolder()
        {
            // フォルダが存在しない場合は作成
            if (!Directory.Exists(LOG_FOLDER))
            {
                Directory.CreateDirectory(LOG_FOLDER);
                AssetDatabase.Refresh();
            }
            
            // Unity Projectタブでフォルダを選択して表示
            Object obj = AssetDatabase.LoadAssetAtPath<Object>(LOG_FOLDER);
            if (obj != null)
            {
                Selection.activeObject = obj;
                EditorGUIUtility.PingObject(obj);
            }
        }
        
        private void DrawOptions()
        {
            EditorGUILayout.LabelField("オプション", EditorStyles.boldLabel);
            autoHeightAdjust = EditorGUILayout.Toggle("自動高さ合わせ", autoHeightAdjust);
            skipExistingThumbnails = EditorGUILayout.Toggle("再撮影をスキップ", skipExistingThumbnails);
            showRetakeWarning = EditorGUILayout.Toggle("再撮影警告文", showRetakeWarning);
            overwriteAvatarList = EditorGUILayout.Toggle("アバターリストを上書きする", overwriteAvatarList);
        }
        
        private void DrawPreview()
        {
            EditorGUILayout.BeginHorizontal();
            EditorGUILayout.LabelField("カメラプレビュー", EditorStyles.boldLabel);
            GUILayout.FlexibleSpace();
            
            // プレビュー中のアバターを撮影ボタン
            if (selectedPreviewIndex >= 0 && selectedPreviewIndex < avatarList.Count)
            {
                var previewAvatar = avatarList[selectedPreviewIndex];
                EditorGUI.BeginDisabledGroup(previewAvatar.sceneObject == null);
                if (GUILayout.Button("このアバターを撮影", GUILayout.Width(130)))
                {
                    CaptureAvatar(previewAvatar);
                }
                EditorGUI.EndDisabledGroup();
            }
            
            if (GUILayout.Button("プレビューを閉じる", GUILayout.Width(150)))
            {
                CleanupPreview();
                showPreview = false;
                selectedPreviewIndex = -1;
                Repaint();
            }
            EditorGUILayout.EndHorizontal();
            
            if (previewTexture != null)
            {
                Rect previewRect = GUILayoutUtility.GetRect(256, 256);
                EditorGUI.DrawPreviewTexture(previewRect, previewTexture, null, ScaleMode.ScaleToFit);
            }
            else
            {
                EditorGUILayout.HelpBox("プレビューするアバターを選択してください", MessageType.Info);
            }
        }
        
        private void DrawAvatarList()
        {
            EditorGUILayout.LabelField($"アバターリスト ({avatarList.Count}体)", EditorStyles.boldLabel);
            
            if (GUILayout.Button("リストを更新"))
            {
                RefreshAvatarList();
            }
            
            // ドラッグ&ドロップエリア
            DrawDragDropArea();
            
            EditorGUILayout.BeginHorizontal();
            if (GUILayout.Button("全選択"))
            {
                foreach (var avatar in avatarList)
                {
                    avatar.isSelected = true;
                }
            }
            if (GUILayout.Button("選択解除"))
            {
                foreach (var avatar in avatarList)
                {
                    avatar.isSelected = false;
                }
            }
            EditorGUILayout.EndHorizontal();
            
            EditorGUILayout.Space(5);
            
            scrollPosition = EditorGUILayout.BeginScrollView(scrollPosition, GUILayout.Height(AVATAR_LIST_HEIGHT));
            
            for (int i = 0; i < avatarList.Count; i++)
            {
                DrawAvatarItem(i);
            }
            
            EditorGUILayout.EndScrollView();
        }
        
        private void DrawAvatarItem(int index)
        {
            var avatar = avatarList[index];
            
            EditorGUILayout.BeginHorizontal();
            
            // チェックボックス
            avatar.isSelected = EditorGUILayout.Toggle(avatar.isSelected, GUILayout.Width(20));
            
            // アバター名
            EditorGUILayout.LabelField(avatar.name, GUILayout.Width(AVATAR_NAME_WIDTH));
            
            // 状態表示
            string status = GetAvatarStatus(avatar);
            if (!string.IsNullOrEmpty(status))
            {
                EditorGUILayout.LabelField(status, GUILayout.Width(STATUS_WIDTH));
            }
            
            GUILayout.FlexibleSpace();
            
            // プレビューボタン
            EditorGUI.BeginDisabledGroup(avatar.sceneObject == null);
            if (GUILayout.Button("プレビュー", GUILayout.Width(BUTTON_WIDTH_LARGE)))
            {
                TogglePreview(index);
            }
            EditorGUI.EndDisabledGroup();
            
            // 個別撮影ボタン
            EditorGUI.BeginDisabledGroup(avatar.sceneObject == null);
            if (GUILayout.Button("撮影", GUILayout.Width(BUTTON_WIDTH_MEDIUM)))
            {
                CaptureAvatar(avatar);
            }
            EditorGUI.EndDisabledGroup();
            
            // 削除ボタン
            if (GUILayout.Button("×", GUILayout.Width(BUTTON_WIDTH_SMALL)))
            {
                if (EditorUtility.DisplayDialog("確認", 
                    $"{avatar.name} をリストから削除しますか？", "削除", "キャンセル"))
                {
                    avatarList.RemoveAt(index);
                    SaveAvatarList();
                    Repaint();
                    return;
                }
            }
            
            // 並び替えボタン
            DrawReorderButtons(index);
            
            EditorGUILayout.EndHorizontal();
        }
        
        /// <summary>
        /// アバターの状態を取得
        /// </summary>
        private string GetAvatarStatus(AvatarInfo avatar)
        {
            if (avatar.sceneObject == null)
            {
                return "(非表示)";
            }
            
            if (!avatar.sceneObject.activeInHierarchy)
            {
                return "(非表示)";
            }
            
            if (File.Exists(avatar.thumbnailPath))
            {
                return "✓撮影済";
            }
            
            return string.Empty;
        }
        
        /// <summary>
        /// 並び替えボタンを描画
        /// </summary>
        private void DrawReorderButtons(int index)
        {
            EditorGUI.BeginDisabledGroup(index == 0);
            if (GUILayout.Button("↑", GUILayout.Width(BUTTON_WIDTH_SMALL)))
            {
                MoveAvatar(index, index - 1);
            }
            EditorGUI.EndDisabledGroup();
            
            EditorGUI.BeginDisabledGroup(index == avatarList.Count - 1);
            if (GUILayout.Button("↓", GUILayout.Width(BUTTON_WIDTH_SMALL)))
            {
                MoveAvatar(index, index + 1);
            }
            EditorGUI.EndDisabledGroup();
        }
        
        private void DrawActionButtons()
        {
            EditorGUILayout.LabelField("撮影実行", EditorStyles.boldLabel);
            
            if (GUILayout.Button("選択アバターのみ撮影", GUILayout.Height(30)))
            {
                CaptureSelectedAvatars();
            }
            

            
            EditorGUILayout.Space(10);
            if (GUILayout.Button("保存フォルダを開く", GUILayout.Height(25)))
            {
                OpenSaveFolder();
            }

                        EditorGUILayout.HelpBox("※ logファイル内の各シーン名に格納されています", MessageType.Info);
        }
        
        #endregion
        
        #region Preview
        
        private void TogglePreview(int index)
        {
            if (selectedPreviewIndex == index && showPreview)
            {
                CleanupPreview();
                showPreview = false;
                selectedPreviewIndex = -1;
            }
            else
            {
                selectedPreviewIndex = index;
                showPreview = true;
                UpdatePreview();
            }
            
            Repaint();
        }
        
        private void UpdatePreview()
        {
            if (selectedPreviewIndex < 0 || selectedPreviewIndex >= avatarList.Count)
            {
                return;
            }
            
            var avatar = avatarList[selectedPreviewIndex];
            if (avatar.sceneObject == null)
            {
                return;
            }
            
            CleanupPreview();
            
            // プレビュー用カメラ作成（一時的）
            GameObject cameraObj = new GameObject("PreviewCamera");
            cameraObj.hideFlags = HideFlags.HideAndDontSave;
            previewCamera = cameraObj.AddComponent<Camera>();
            previewCamera.backgroundColor = backgroundColor;
            previewCamera.clearFlags = CameraClearFlags.SolidColor;
            
            // カメラ位置調整
            PositionCamera(previewCamera, avatar.sceneObject);
            
            // RenderTexture作成
            previewRT = new RenderTexture(512, 512, 24);
            previewCamera.targetTexture = previewRT;
            
            // プレビュー画像生成
            previewCamera.Render();
            
            RenderTexture.active = previewRT;
            previewTexture = new Texture2D(512, 512, TextureFormat.RGB24, false);
            previewTexture.ReadPixels(new Rect(0, 0, 512, 512), 0, 0);
            previewTexture.Apply();
            RenderTexture.active = null;
        }
        
        private void CleanupPreview()
        {
            if (previewCamera != null)
            {
                DestroyImmediate(previewCamera.gameObject);
                previewCamera = null;
            }
            
            if (previewRT != null)
            {
                previewRT.Release();
                DestroyImmediate(previewRT);
                previewRT = null;
            }
            
            if (previewTexture != null)
            {
                DestroyImmediate(previewTexture);
                previewTexture = null;
            }
        }
        
        #endregion
        
        #region Drag & Drop
        
        private void DrawDragDropArea()
        {
            Event evt = Event.current;
            Rect dropArea = GUILayoutUtility.GetRect(0, 50, GUILayout.ExpandWidth(true));
            
            GUI.Box(dropArea, "アバターをここにドラッグ&ドロップ\n(GameObject または Prefab)", EditorStyles.helpBox);
            
            switch (evt.type)
            {
                case EventType.DragUpdated:
                case EventType.DragPerform:
                    if (!dropArea.Contains(evt.mousePosition))
                        break;
                    
                    DragAndDrop.visualMode = DragAndDropVisualMode.Copy;
                    
                    if (evt.type == EventType.DragPerform)
                    {
                        DragAndDrop.AcceptDrag();
                        
                        foreach (var draggedObject in DragAndDrop.objectReferences)
                        {
                            GameObject gameObject = draggedObject as GameObject;
                            if (gameObject != null)
                            {
                                AddAvatarFromGameObject(gameObject, false); // 手動追加は条件チェックなし
                            }
                        }
                        
                        SaveAvatarList();
                    }
                    
                    evt.Use();
                    break;
            }
        }
        
        private void AddAvatarFromGameObject(GameObject obj, bool checkConditions = true)
        {
            if (obj == null)
            {
                return;
            }
            
            // 手動追加の場合は条件チェックをスキップ
            if (checkConditions)
            {
                // アバター判定
                if (!IsAvatar(obj))
                {
                    EditorUtility.DisplayDialog("エラー", 
                        $"{obj.name} はアバターとして認識できません。\n\n条件:\n・Animatorを持つ\n・Humanoidである\n・Armature階層を持つ", 
                        "OK");
                    return;
                }
            }
            
            // 既に登録済みかチェック
            if (avatarList.Any(a => a.name == obj.name))
            {
                EditorUtility.DisplayDialog("情報", $"{obj.name} は既にリストに登録されています", "OK");
                return;
            }
            
            var currentSceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            var newAvatar = new AvatarInfo
            {
                name = obj.name,
                sceneObject = obj,
                thumbnailPath = GetThumbnailPath(currentSceneName, obj.name),
                isSelected = true
            };
            
            avatarList.Add(newAvatar);
            UnityEngine.Debug.Log($"[AvatarThumbnail] アバターを追加: {obj.name}");
        }
        
        #endregion
        
        #region Avatar List Management
        
        private void RefreshAvatarList()
        {
            var currentSceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            
            // シーン内のアバターを検索
            var rootObjects = UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects();
            List<GameObject> foundAvatars = new List<GameObject>();
            
            foreach (var root in rootObjects)
            {
                // 自動検出では条件チェックあり
                if (IsAvatar(root))
                {
                    foundAvatars.Add(root);
                }
            }
            
            // 常にリストをクリアして上書き（別シーンのアバターを削除）
            avatarList.Clear();
            
            foreach (var avatarObj in foundAvatars)
            {
                avatarList.Add(new AvatarInfo
                {
                    name = avatarObj.name,
                    sceneObject = avatarObj,
                    thumbnailPath = GetThumbnailPath(currentSceneName, avatarObj.name),
                    isSelected = true
                });
            }
            
            SaveAvatarList();
            UnityEngine.Debug.Log($"[AvatarThumbnail] {currentSceneName} のアバターリスト更新: {avatarList.Count}体");
        }
        
        private bool IsAvatar(GameObject obj)
        {
            Animator animator = obj.GetComponent<Animator>();
            if (animator == null)
            {
                return false;
            }
            
            if (!animator.isHuman)
            {
                return false;
            }
            
            Transform armature = FindArmatureInChildren(obj.transform);
            if (armature == null)
            {
                return false;
            }
            
            return true;
        }
        
        private Transform FindArmatureInChildren(Transform parent)
        {
            if (parent.name.Equals("Armature", System.StringComparison.OrdinalIgnoreCase))
            {
                return parent;
            }
            
            foreach (Transform child in parent)
            {
                Transform found = FindArmatureInChildren(child);
                if (found != null)
                {
                    return found;
                }
            }
            
            return null;
        }
        
        private void MoveAvatar(int fromIndex, int toIndex)
        {
            var avatar = avatarList[fromIndex];
            avatarList.RemoveAt(fromIndex);
            avatarList.Insert(toIndex, avatar);
            SaveAvatarList();
        }
        
        /// <summary>
        /// Animatorの状態を保存
        /// </summary>
        private Dictionary<GameObject, AnimatorState> SaveAnimatorStates()
        {
            var states = new Dictionary<GameObject, AnimatorState>();
            
            foreach (var avatar in avatarList)
            {
                if (avatar.sceneObject != null)
                {
                    Animator animator = avatar.sceneObject.GetComponent<Animator>();
                    if (animator != null)
                    {
                        states[avatar.sceneObject] = new AnimatorState
                        {
                            isActive = avatar.sceneObject.activeSelf,
                            isEnabled = animator.enabled
                        };
                    }
                    else
                    {
                        states[avatar.sceneObject] = new AnimatorState
                        {
                            isActive = avatar.sceneObject.activeSelf,
                            isEnabled = false
                        };
                    }
                }
            }
            
            return states;
        }
        
        /// <summary>
        /// Animatorの状態を復元
        /// </summary>
        private void RestoreAnimatorStates(Dictionary<GameObject, AnimatorState> states)
        {
            foreach (var kvp in states)
            {
                if (kvp.Key != null)
                {
                    // アクティブ状態を復元
                    kvp.Key.SetActive(kvp.Value.isActive);
                    
                    // Animator状態を復元
                    Animator animator = kvp.Key.GetComponent<Animator>();
                    if (animator != null)
                    {
                        animator.enabled = kvp.Value.isEnabled;
                        if (kvp.Value.isEnabled)
                        {
                            animator.Rebind();
                        }
                    }
                }
            }
        }
        
        /// <summary>
        /// Animator状態の保存用構造体
        /// </summary>
        private struct AnimatorState
        {
            public bool isActive;
            public bool isEnabled;
        }
        
        #endregion
        
        #region Capture
        
        private void CaptureSelectedAvatars()
        {
            var selectedAvatars = avatarList.Where(a => a.isSelected && a.sceneObject != null).ToList();
            
            if (selectedAvatars.Count == 0)
            {
                EditorUtility.DisplayDialog("エラー", "撮影対象のアバターがありません", "OK");
                return;
            }
            
            // 撮影済みアバターをカウント
            int alreadyCapturedCount = 0;
            if (skipExistingThumbnails)
            {
                alreadyCapturedCount = selectedAvatars.Count(a => File.Exists(a.thumbnailPath));
            }
            
            // 再撮影警告文がオンで、撮影済みアバターがある場合は警告を表示
            if (showRetakeWarning && alreadyCapturedCount > 0)
            {
                string message = $"撮影済のアバターが {alreadyCapturedCount} 体含まれるため、スキップされますがよろしいですか？\n\n" +
                                 "※プレビュー表示と撮影画像はアバター表示が異なる場合がございます\n" +
                                 "※この警告文は「再撮影警告文」オプションをオフで非表示にできます";
                
                if (!EditorUtility.DisplayDialog("確認", message, "撮影する", "キャンセル"))
                {
                    return;
                }
            }
            
            CaptureAvatarsBatch(selectedAvatars);
        }
        
        private void CaptureAvatarsBatch(List<AvatarInfo> avatars)
        {
            if (avatars.Count == 0)
            {
                EditorUtility.DisplayDialog("エラー", "撮影対象のアバターがありません", "OK");
                return;
            }
            
            // 撮影前の状態を保存（表示状態とAnimatorの有効状態）
            Dictionary<GameObject, bool> originalStates = new Dictionary<GameObject, bool>();
            Dictionary<GameObject, bool> originalAnimatorStates = new Dictionary<GameObject, bool>();
            
            foreach (var avatar in avatarList)
            {
                if (avatar.sceneObject != null)
                {
                    originalStates[avatar.sceneObject] = avatar.sceneObject.activeSelf;
                    
                    Animator animator = avatar.sceneObject.GetComponent<Animator>();
                    if (animator != null)
                    {
                        originalAnimatorStates[avatar.sceneObject] = animator.enabled;
                    }
                }
            }
            
            int capturedCount = 0;
            int skippedCount = 0;
            int totalCount = avatars.Count;
            
            try
            {
                for (int i = 0; i < avatars.Count; i++)
                {
                    var avatar = avatars[i];
                    
                    EditorUtility.DisplayProgressBar("撮影中", 
                        $"{avatar.name} を撮影中... ({i + 1}/{totalCount})", 
                        (float)i / totalCount);
                    
                    if (skipExistingThumbnails && File.Exists(avatar.thumbnailPath))
                    {
                        skippedCount++;
                        continue;
                    }
                    
                    if (ExecuteCapture(avatar))
                    {
                        capturedCount++;
                    }
                }
            }
            finally
            {
                // 元の状態に戻す（表示状態とAnimatorの有効状態）
                foreach (var kvp in originalStates)
                {
                    if (kvp.Key != null)
                    {
                        kvp.Key.SetActive(kvp.Value);
                    }
                }
                
                foreach (var kvp in originalAnimatorStates)
                {
                    if (kvp.Key != null)
                    {
                        Animator animator = kvp.Key.GetComponent<Animator>();
                        if (animator != null)
                        {
                            animator.enabled = kvp.Value;
                            if (kvp.Value)
                            {
                                // Animatorを再有効化時にリセット
                                animator.Rebind();
                            }
                        }
                    }
                }
                
                CleanupCaptureObjects();
                EditorUtility.ClearProgressBar();
                
                // エディタを更新
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
            }
            
            SaveAvatarList();
            AssetDatabase.Refresh();
            
            EditorUtility.DisplayDialog("撮影完了", 
                $"撮影完了: {capturedCount}体\nスキップ: {skippedCount}体\n\n※アバターによっては正しく撮影されていない場合があります。\nその際は個別に設定を行い撮影をしてください。", "OK");
        }
        
        private void CaptureAvatar(AvatarInfo avatar)
        {
            if (skipExistingThumbnails && File.Exists(avatar.thumbnailPath))
            {
                if (!EditorUtility.DisplayDialog("確認", 
                    $"{avatar.name}は既に撮影済みです。\n再撮影しますか?", "撮影する", "キャンセル"))
                {
                    return;
                }
            }
            
            // 撮影前の状態を保存（表示状態とAnimatorの有効状態）
            Dictionary<GameObject, bool> originalStates = new Dictionary<GameObject, bool>();
            Dictionary<GameObject, bool> originalAnimatorStates = new Dictionary<GameObject, bool>();
            
            foreach (var av in avatarList)
            {
                if (av.sceneObject != null)
                {
                    originalStates[av.sceneObject] = av.sceneObject.activeSelf;
                    
                    Animator animator = av.sceneObject.GetComponent<Animator>();
                    if (animator != null)
                    {
                        originalAnimatorStates[av.sceneObject] = animator.enabled;
                    }
                }
            }
            
            try
            {
                EditorUtility.DisplayProgressBar("撮影中", $"{avatar.name} を撮影中...", 0.5f);
                
                bool success = ExecuteCapture(avatar);
                
                if (success)
                {
                    SaveAvatarList();
                    AssetDatabase.Refresh();
                    EditorUtility.DisplayDialog("撮影完了", $"{avatar.name}の撮影が完了しました\n\n※アバターによっては正しく撮影されていない場合があります。\nその際は個別に設定を行い撮影をしてください。", "OK");
                }
                else
                {
                    EditorUtility.DisplayDialog("撮影失敗", $"{avatar.name}の撮影に失敗しました", "OK");
                }
            }
            finally
            {
                // 元の状態に戻す（表示状態とAnimatorの有効状態）
                foreach (var kvp in originalStates)
                {
                    if (kvp.Key != null)
                    {
                        kvp.Key.SetActive(kvp.Value);
                    }
                }
                
                foreach (var kvp in originalAnimatorStates)
                {
                    if (kvp.Key != null)
                    {
                        Animator animator = kvp.Key.GetComponent<Animator>();
                        if (animator != null)
                        {
                            animator.enabled = kvp.Value;
                            if (kvp.Value)
                            {
                                // Animatorを再有効化時にリセット
                                animator.Rebind();
                            }
                        }
                    }
                }
                
                CleanupCaptureObjects();
                EditorUtility.ClearProgressBar();
                
                // エディタを更新
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
            }
        }
        
        private bool ExecuteCapture(AvatarInfo avatar)
        {
            if (avatar.sceneObject == null)
            {
                UnityEngine.Debug.LogError($"[AvatarThumbnail] {avatar.name}: シーンオブジェクトが見つかりません");
                return false;
            }
            
            try
            {
                // 全アバターを非表示にして、Animatorを停止
                foreach (var av in avatarList)
                {
                    if (av.sceneObject != null)
                    {
                        av.sceneObject.SetActive(false);
                        
                        // Animatorを一時停止してアニメーション状態をリセット
                        Animator animator = av.sceneObject.GetComponent<Animator>();
                        if (animator != null)
                        {
                            animator.enabled = false;
                        }
                    }
                }
                
                // エディタを1フレーム更新（非表示が確実に反映されるまで待つ）
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
                
                // 撮影対象のアバターのみ表示
                avatar.sceneObject.SetActive(true);
                
                // Animatorを初期状態で有効化
                Animator targetAnimator = avatar.sceneObject.GetComponent<Animator>();
                if (targetAnimator != null)
                {
                    targetAnimator.enabled = true;
                    // Animatorを初期状態にリセット（レイヤー0、ステート0）
                    targetAnimator.Rebind();
                    targetAnimator.Update(0);
                }
                
                // もう1フレーム待機して表示が完全に反映されるのを待つ
                EditorApplication.QueuePlayerLoopUpdate();
                SceneView.RepaintAll();
                
                // 撮影用カメラとライトを作成
                SetupCaptureObjects();
                
                // カメラ位置調整
                PositionCamera(captureCamera, avatar.sceneObject);
                
                // 撮影
                RenderTexture rt = new RenderTexture(THUMBNAIL_SIZE, THUMBNAIL_SIZE, 24);
                captureCamera.targetTexture = rt;
                captureCamera.Render();
                
                RenderTexture.active = rt;
                Texture2D screenshot = new Texture2D(THUMBNAIL_SIZE, THUMBNAIL_SIZE, TextureFormat.RGB24, false);
                screenshot.ReadPixels(new Rect(0, 0, THUMBNAIL_SIZE, THUMBNAIL_SIZE), 0, 0);
                screenshot.Apply();
                
                // ファイル保存
                byte[] bytes = screenshot.EncodeToPNG();
                string directory = Path.GetDirectoryName(avatar.thumbnailPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                File.WriteAllBytes(avatar.thumbnailPath, bytes);
                
                // クリーンアップ
                captureCamera.targetTexture = null;
                RenderTexture.active = null;
                DestroyImmediate(rt);
                DestroyImmediate(screenshot);
                
                UnityEngine.Debug.Log($"[AvatarThumbnail] 撮影成功: {avatar.name} -> {avatar.thumbnailPath}");
                return true;
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"[AvatarThumbnail] 撮影エラー: {avatar.name} - {e.Message}\n{e.StackTrace}");
                return false;
            }
        }
        
        private void SetupCaptureObjects()
        {
            if (captureCamera == null)
            {
                GameObject cameraObj = new GameObject("ThumbnailCaptureCamera");
                cameraObj.hideFlags = HideFlags.HideAndDontSave;
                captureCamera = cameraObj.AddComponent<Camera>();
                captureCamera.backgroundColor = backgroundColor;
                captureCamera.clearFlags = CameraClearFlags.SolidColor;
                captureCamera.fieldOfView = 60;
            }
            
            if (captureLight == null)
            {
                GameObject lightObj = new GameObject("ThumbnailCaptureLight");
                lightObj.hideFlags = HideFlags.HideAndDontSave;
                captureLight = lightObj.AddComponent<Light>();
                captureLight.type = LightType.Directional;
                captureLight.intensity = 1f;
                captureLight.transform.rotation = Quaternion.Euler(50, -30, 0);
            }
        }
        
        private void CleanupCaptureObjects()
        {
            if (captureCamera != null)
            {
                DestroyImmediate(captureCamera.gameObject);
                captureCamera = null;
            }
            
            if (captureLight != null)
            {
                DestroyImmediate(captureLight.gameObject);
                captureLight = null;
            }
        }
        
        private void PositionCamera(Camera cam, GameObject avatar)
        {
            Bounds bounds = CalculateBounds(avatar);
            Vector3 center = bounds.center;
            
            if (autoHeightAdjust)
            {
                center.y = bounds.center.y;
            }
            
            if (!usePanYaw)
            {
                // 軸移動モード：スロットル式の水平移動
                // カメラ位置 = アバター中心 + XYZ軸オフセット
                // カメラの向きは回転設定（cameraRotation）で決まる
                
                cam.transform.position = center + cameraPosition;
                
                // 回転設定に基づいた方向を向く
                Quaternion lookRotation = Quaternion.Euler(cameraRotation.x, cameraRotation.y, 0);
                cam.transform.rotation = lookRotation;
            }
            else
            {
                // 回転モード：現在位置を保持したまま、その場で回転
                // カメラ位置は軸移動で設定した位置（cameraPosition）を保持
                
                cam.transform.position = center + cameraPosition;
                
                // 回転設定に基づいた方向を向く（その場で回転）
                Quaternion lookRotation = Quaternion.Euler(cameraRotation.x, cameraRotation.y, 0);
                cam.transform.rotation = lookRotation;
            }
        }
        
        private Bounds CalculateBounds(GameObject obj)
        {
            Renderer[] renderers = obj.GetComponentsInChildren<Renderer>();
            if (renderers.Length == 0)
            {
                return new Bounds(obj.transform.position, Vector3.one);
            }
            
            Bounds bounds = renderers[0].bounds;
            foreach (var renderer in renderers)
            {
                bounds.Encapsulate(renderer.bounds);
            }
            return bounds;
        }
        
        #endregion
        
        #region Data Management
        
        private string GetThumbnailPath(string sceneName, string avatarName)
        {
            return Path.Combine(LOG_FOLDER, sceneName, $"{avatarName}.png").Replace("\\", "/");
        }
        
        private void LoadAvatarList()
        {
            var currentSceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
            
            string dbPath = Path.Combine(LOG_FOLDER, DATABASE_FILE);
            if (!File.Exists(dbPath))
            {
                return;
            }
            
            try
            {
                string json = File.ReadAllText(dbPath);
                
                if (string.IsNullOrWhiteSpace(json))
                {
                    return;
                }
                
                var database = JsonUtility.FromJson<AvatarDatabase>(json);
                
                if (database == null || database.projects == null)
                {
                    return;
                }
                
                // 現在のシーンのプロジェクトデータのみ読み込む
                var project = database.projects.FirstOrDefault(p => p.projectName == currentSceneName);
                
                if (project != null && project.avatars != null)
                {
                    avatarList.Clear(); // 必ずクリア
                    foreach (var avatarData in project.avatars)
                    {
                        GameObject sceneObj = GameObject.Find(avatarData.name);
                        
                        avatarList.Add(new AvatarInfo
                        {
                            name = avatarData.name,
                            sceneObject = sceneObj,
                            thumbnailPath = avatarData.thumbnailPath,
                            isSelected = true
                        });
                    }
                }
                else
                {
                    // 現在のシーンのデータがない場合はリストをクリア
                    avatarList.Clear();
                }
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"[AvatarThumbnail] データベース読み込みエラー: {e.Message}");
            }
        }
        
        private void SaveAvatarList()
        {
            string dbPath = Path.Combine(LOG_FOLDER, DATABASE_FILE);
            
            try
            {
                AvatarDatabase database;
                
                if (File.Exists(dbPath))
                {
                    string existingJson = File.ReadAllText(dbPath);
                    
                    if (string.IsNullOrWhiteSpace(existingJson))
                    {
                        database = new AvatarDatabase { projects = new List<ProjectData>() };
                    }
                    else
                    {
                        try
                        {
                            database = JsonUtility.FromJson<AvatarDatabase>(existingJson);
                            
                            if (database == null || database.projects == null)
                            {
                                database = new AvatarDatabase { projects = new List<ProjectData>() };
                            }
                        }
                        catch
                        {
                            database = new AvatarDatabase { projects = new List<ProjectData>() };
                        }
                    }
                }
                else
                {
                    database = new AvatarDatabase { projects = new List<ProjectData>() };
                }
                
                var currentSceneName = UnityEngine.SceneManagement.SceneManager.GetActiveScene().name;
                var project = database.projects.FirstOrDefault(p => p.projectName == currentSceneName);
                
                if (project == null)
                {
                    project = new ProjectData { projectName = currentSceneName, avatars = new List<AvatarData>() };
                    database.projects.Add(project);
                }
                
                project.avatars.Clear();
                foreach (var avatar in avatarList)
                {
                    project.avatars.Add(new AvatarData
                    {
                        name = avatar.name ?? "",
                        thumbnailPath = avatar.thumbnailPath ?? "",
                        memo = null
                    });
                }
                
                string directory = Path.GetDirectoryName(dbPath);
                if (!Directory.Exists(directory))
                {
                    Directory.CreateDirectory(directory);
                }
                
                string json = JsonUtility.ToJson(database, true);
                File.WriteAllText(dbPath, json);
            }
            catch (System.Exception e)
            {
                UnityEngine.Debug.LogError($"[AvatarThumbnail] データベース保存エラー: {e.Message}\n{e.StackTrace}");
            }
        }
        
        #endregion
        
        #region Data Classes
        
        [System.Serializable]
        private class AvatarInfo
        {
            public string name;
            public GameObject sceneObject; // シーン内のGameObject参照
            public string thumbnailPath;
            public bool isSelected;
        }
        
        [System.Serializable]
        private class AvatarDatabase
        {
            public List<ProjectData> projects = new List<ProjectData>();
        }
        
        [System.Serializable]
        private class ProjectData
        {
            public string projectName;
            public List<AvatarData> avatars = new List<AvatarData>();
        }
        
        [System.Serializable]
        private class AvatarData
        {
            public string name;
            public string thumbnailPath;
            public string memo;
        }
        
        #endregion
    }
}
#endif
