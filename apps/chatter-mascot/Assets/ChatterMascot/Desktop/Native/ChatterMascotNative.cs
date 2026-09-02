#if UNITY_STANDALONE_OSX || UNITY_EDITOR_OSX
using System;
using System.Runtime.InteropServices;
using UnityEngine;

namespace ChatterMascot.Desktop.Native
{
    /// <summary>
    /// ネイティブから返ってくるイベント1件。
    ///
    /// ★ <b><c>static</c> メソッドを <c>[MonoPInvokeCallback]</c> 付きで渡すこと。</b>
    ///   インスタンスメソッドやクロージャは Mono では動くが IL2CPP で落ちる。
    /// </summary>
    internal delegate void NativeEventCallback([MarshalAs(UnmanagedType.LPUTF8Str)] string json);

    /// <summary>
    /// <c>ChatterMascotNative.bundle</c>（#75）への口。
    ///
    /// ★★ <b>バンドルが無くても起動すること。</b> <c>.bundle</c> は git に入れていない
    ///   （バイナリはレビューできず、ソースとの一致を CI で検証できない）ので、
    ///   <b>新規クローンで作り忘れる</b>のは普通に起きる。そのとき落ちるのは
    ///   「メニューバーに出ない」だけであって、マスコットは出て喋る。
    ///
    /// ★ <b>可用性の判定は初回1回だけ。</b> <c>try/catch</c> を毎フレーム回さない
    ///   （イベントの drain は <c>Update</c> から走る）。
    ///
    /// ★ <b><c>CM_Version</c> の戻り値を <c>string</c> で受けないこと。</b> P/Invoke の
    ///   戻り値 <c>string</c> は、マーシャラが受け取った領域を<b>解放しようとする</b>。
    ///   ObjC 側は静的な文字列を返しているので、解放させると落ちる。
    /// </summary>
    internal static class ChatterMascotNative
    {
        /// <summary>
        /// ★ <c>Assets/Plugins/macOS/ChatterMascotNative.bundle</c> の名前と揃えること。
        ///   拡張子も <c>lib</c> の接頭辞も付けない。
        /// </summary>
        private const string Library = "ChatterMascotNative";

        private static bool _probed;
        private static bool _available;

        /// <summary>
        /// 読み込めたか。<b>読めなければ警告を1本だけ出して false を返し続ける</b>。
        /// </summary>
        internal static bool IsAvailable
        {
            get
            {
                if (_probed) return _available;
                _probed = true;

                try
                {
                    var version = Marshal.PtrToStringAnsi(CM_Version());
                    _available = true;
                    Debug.Log($"[Native] プラグインを読み込みました (version={version})");
                }
                catch (DllNotFoundException e)
                {
                    // ★ 1行に畳むこと。 複数行のログは scripts の grep で2行目以降が消える
                    Debug.LogWarning(
                        "[Native] ChatterMascotNative.bundle が見つかりません。" +
                        "メニューバー常駐とグローバルショートカットは動きません" +
                        "（./scripts/build-native.sh で作れます）: " + OneLine(e.Message));
                }
                catch (EntryPointNotFoundException e)
                {
                    Debug.LogWarning(
                        "[Native] ChatterMascotNative.bundle が古いようです。" +
                        "作り直してください（./scripts/build-native.sh）: " + OneLine(e.Message));
                }

                return _available;
            }
        }

        internal static string OneLine(string text)
        {
            if (string.IsNullOrEmpty(text)) return string.Empty;
            return text.Replace("\r", " ").Replace("\n", " ");
        }

        [DllImport(Library)]
        internal static extern void CM_SetEventCallback(NativeEventCallback callback);

        [DllImport(Library)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CM_Initialize();

        [DllImport(Library)]
        internal static extern void CM_Shutdown();

        /// <summary>★ 解放しない静的な領域（→ 型の doc）</summary>
        [DllImport(Library)]
        internal static extern IntPtr CM_Version();

        /// <summary>0 = regular（Dock に出る） / 1 = accessory（出ない）</summary>
        [DllImport(Library)]
        internal static extern void CM_SetActivationPolicy(int policy);

        [DllImport(Library)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CM_StatusItemShow([MarshalAs(UnmanagedType.LPUTF8Str)] string menuJson);

        [DllImport(Library)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CM_StatusItemUpdate([MarshalAs(UnmanagedType.LPUTF8Str)] string menuJson);

        [DllImport(Library)]
        internal static extern void CM_StatusItemHide();

        /// <summary>0 = 成功。それ以外は OSStatus（-9878 = 他のアプリが取っている）</summary>
        [DllImport(Library)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CM_PanelShow(int panelId, [MarshalAs(UnmanagedType.LPUTF8Str)] string schemaJson);

        [DllImport(Library)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CM_PanelUpdate(int panelId, [MarshalAs(UnmanagedType.LPUTF8Str)] string schemaJson);

        [DllImport(Library)]
        internal static extern void CM_PanelHide(int panelId);

        [DllImport(Library)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CM_PanelIsVisible(int panelId);

        [DllImport(Library)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CM_OpenFilePanel([MarshalAs(UnmanagedType.LPUTF8Str)] string optionsJson);

        [DllImport(Library)]
        [return: MarshalAs(UnmanagedType.I1)]
        internal static extern bool CM_Confirm([MarshalAs(UnmanagedType.LPUTF8Str)] string optionsJson);

        [DllImport(Library)]
        internal static extern int CM_HotKeyRegister(int id, uint keyCode, uint modifiers);

        [DllImport(Library)]
        internal static extern void CM_HotKeyUnregister(int id);
    }
}
#endif
