#if UNITY_EDITOR
// ============================================================================
//  VRChat SDK アイコン設定(リフレクションで SDK API を呼ぶ。失敗時は手動案内へ)
// ============================================================================
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using UnityEditor;
using UnityEngine;
using Object = UnityEngine.Object;

namespace LollipopCandy.AvatarHub
{
    internal sealed class SdkIconResult
    {
        public bool ok;
        public string message;
    }

    /// <summary>
    /// 撮影画像をVRChat SDKのアバターアイコンに設定する。
    /// SDKのAPIシグネチャはバージョンで変わりうるため、リフレクションで引数を組み立てる。
    /// 失敗時は呼び出し側が手動設定へ誘導する。
    /// </summary>
    internal static class SdkIconBridge
    {
        private const string ApiTypeName = "VRC.SDKBase.Editor.Api.VRCApi";

        /// <summary>SDKのアイコン表示(4:3)に合わせて中央を切り出した一時PNGを作る。</summary>
        public static string CreateSdkImage(string sourcePath)
        {
            Texture2D src = null;
            Texture2D dst = null;
            try
            {
                src = new Texture2D(2, 2, TextureFormat.RGB24, false);
                if (!src.LoadImage(File.ReadAllBytes(sourcePath))) return null;

                // 4:3 で中央を切り出す(縦長なら上下を、横長なら左右を落とす)
                int w = src.width;
                int h = Mathf.RoundToInt(w * 3f / 4f);
                if (h > src.height)
                {
                    h = src.height;
                    w = Mathf.RoundToInt(h * 4f / 3f);
                }
                int x = (src.width - w) / 2;
                int y = (src.height - h) / 2;

                dst = new Texture2D(w, h, TextureFormat.RGB24, false);
                dst.SetPixels(src.GetPixels(x, y, w, h));
                dst.Apply(false);

                string path = Path.GetFullPath("Temp/AvatarThumbnailManager_sdk_icon.png");
                Directory.CreateDirectory(Path.GetDirectoryName(path));
                File.WriteAllBytes(path, dst.EncodeToPNG());
                return path;
            }
            catch (System.Exception e)
            {
                Debug.LogError("[AvatarThumbnail] SDK用画像の作成に失敗: " + e.Message);
                return null;
            }
            finally
            {
                if (src != null) Object.DestroyImmediate(src);
                if (dst != null) Object.DestroyImmediate(dst);
            }
        }

        /// <summary>手動設定の準備: SDKパネルを開き、画像の場所を開いてパスをコピーする。</summary>
        public static void PrepareManual(string imagePath)
        {
            EditorApplication.ExecuteMenuItem("VRChat SDK/Show Control Panel");
            GUIUtility.systemCopyBuffer = imagePath;
            EditorUtility.RevealInFinder(imagePath);
        }

        public static async Task<SdkIconResult> TryUploadAsync(string blueprintId, string imagePath)
        {
            try
            {
                System.Type api = FindType(ApiTypeName);
                if (api == null) return Fail("VRChat SDK(VRCApi)が見つかりません。");

                MethodInfo update = api.GetMethods(BindingFlags.Public | BindingFlags.Static)
                    .FirstOrDefault(m => m.Name == "UpdateAvatarImage");
                if (update == null) return Fail("このSDKバージョンには UpdateAvatarImage がありません。");

                object avatar = null;
                ParameterInfo[] ps = update.GetParameters();
                var args = new object[ps.Length];
                for (int i = 0; i < ps.Length; i++)
                {
                    ParameterInfo p = ps[i];
                    string pn = p.Name.ToLowerInvariant();
                    if (p.ParameterType == typeof(string))
                    {
                        bool isPath = pn.Contains("path") || pn.Contains("file") || pn.Contains("image");
                        args[i] = isPath ? imagePath : blueprintId;
                    }
                    else if (p.ParameterType.Name == "VRCAvatar")
                    {
                        if (avatar == null) avatar = await FetchAvatarAsync(api, blueprintId);
                        if (avatar == null) return Fail("アバター情報を取得できませんでした(SDKへのログイン・所有アバターか確認してください)。");
                        args[i] = avatar;
                    }
                    else
                    {
                        args[i] = DefaultFor(p);
                    }
                }

                await InvokeAsync(update, args);
                return new SdkIconResult { ok = true, message = "アバターアイコンを更新しました。" };
            }
            catch (System.Exception e)
            {
                return Fail(Unwrap(e).Message);
            }
        }

        private static async Task<object> FetchAvatarAsync(System.Type api, string blueprintId)
        {
            MethodInfo get = api.GetMethods(BindingFlags.Public | BindingFlags.Static)
                .FirstOrDefault(m => m.Name == "GetAvatar" && m.GetParameters().Length > 0
                                     && m.GetParameters()[0].ParameterType == typeof(string));
            if (get == null) return null;

            ParameterInfo[] ps = get.GetParameters();
            var args = new object[ps.Length];
            args[0] = blueprintId;
            for (int i = 1; i < ps.Length; i++)
            {
                args[i] = ps[i].ParameterType == typeof(bool) ? (object)true : DefaultFor(ps[i]);
            }
            return await InvokeAsync(get, args);
        }

        private static async Task<object> InvokeAsync(MethodInfo method, object[] args)
        {
            object ret = method.Invoke(null, args);
            Task task = ret as Task;
            if (task == null) return ret;

            await task;
            PropertyInfo result = task.GetType().GetProperty("Result");
            return result != null ? result.GetValue(task, null) : null;
        }

        private static object DefaultFor(ParameterInfo p)
        {
            if (p.ParameterType == typeof(System.Threading.CancellationToken))
            {
                return default(System.Threading.CancellationToken);
            }
            if (p.HasDefaultValue)
            {
                object d = p.DefaultValue;
                if (d == null || d is System.DBNull || d == Missing.Value) return null;
                return d;
            }
            return p.ParameterType.IsValueType ? System.Activator.CreateInstance(p.ParameterType) : null;
        }

        private static System.Type FindType(string fullName)
        {
            foreach (Assembly asm in System.AppDomain.CurrentDomain.GetAssemblies())
            {
                System.Type t = asm.GetType(fullName, false);
                if (t != null) return t;
            }
            return null;
        }

        private static System.Exception Unwrap(System.Exception e)
        {
            while (e is TargetInvocationException && e.InnerException != null) e = e.InnerException;
            return e;
        }

        private static SdkIconResult Fail(string message)
        {
            return new SdkIconResult { ok = false, message = message };
        }
    }
}
#endif
