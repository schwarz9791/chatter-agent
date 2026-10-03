using System;
using System.Collections.Generic;
using System.IO;
using ChatterMascot.Settings;
using ChatterMascot.Ui;
using ChatterMascot.Vrm;
using UnityEngine;

namespace ChatterMascot.Desktop
{
    /// <summary>
    /// ChatterAgent からの依頼（位置と大きさのリセット・モーションの確認）を受け、
    /// 確認用のモーション一覧を公開する。
    ///
    /// ★ <b>ファイルの依頼箱にしている。</b> OS 固有の IPC を使わないので、Windows でも同じ形で受けられる。
    /// ★ <b>起動前に置かれた依頼は実行しない。</b> 古い「リセット」が次の起動で効くと驚くので、
    ///   起動時に消す。
    /// ★ <b><see cref="MonoBehaviour"/> をシーンに置かないこと</b>（→ <see cref="VrmDragHandleBinder"/>）。
    /// </summary>
    public static class MascotInbox
    {
        private const string RequestsDirectory = "requests";
        private const string MotionsFile = "motions.json";

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            if (Application.isEditor) return;

            var directory = Path.GetDirectoryName(WindowGeometry.ResolveStatePath());
            if (string.IsNullOrEmpty(directory)) return;

            var go = new GameObject(nameof(MascotInbox)) { hideFlags = HideFlags.HideAndDontSave };
            UnityEngine.Object.DontDestroyOnLoad(go);
            go.AddComponent<Inbox>().Root = directory;
        }

        private sealed class Inbox : MonoBehaviour
        {
            // ponytail: 0.5 秒のポーリング。ファイル監視は使わない（依頼は人の操作で稀にしか来ない）
            private const float PollSeconds = 0.5f;

            internal string Root;

            private string _requests;
            private string _motionsPath;
            private VrmCharacter _character;
            private IReadOnlyList<MotionClip> _published;
            private float _nextPollAt;

            /// <summary>読み・消しに失敗した名前。警告を毎回出さないために覚える。</summary>
            private readonly HashSet<string> _warned = new HashSet<string>(StringComparer.Ordinal);

            private void Start()
            {
                _requests = Path.Combine(Root, RequestsDirectory);
                _motionsPath = Path.Combine(Root, MotionsFile);

                try
                {
                    if (System.IO.Directory.Exists(_requests))
                    {
                        foreach (var path in System.IO.Directory.GetFiles(_requests, "*.json")) File.Delete(path);
                    }
                    if (File.Exists(_motionsPath)) File.Delete(_motionsPath);
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Mascot] 依頼箱の掃除に失敗しました: " + e.Message);
                }
            }

            private void Update()
            {
                if (Time.realtimeSinceStartup < _nextPollAt) return;
                _nextPollAt = Time.realtimeSinceStartup + PollSeconds;

                PublishMotions();
                Drain();
            }

            private VrmCharacter Character()
            {
                if (_character == null) _character = FindFirstObjectByType<VrmCharacter>(FindObjectsInactive.Include);
                return _character;
            }

            /// <summary>一覧が初めて読めたとき（と、参照が変わったとき）だけ書く。</summary>
            private void PublishMotions()
            {
                var character = Character();
                var clips = character != null ? character.MotionClips : null;
                if (clips == null || ReferenceEquals(clips, _published)) return;

                try
                {
                    WriteAtomically(_motionsPath, MascotRequest.MotionsJson(clips));
                    _published = clips;
                }
                catch (Exception e)
                {
                    Debug.LogWarning("[Mascot] motions.json を書けませんでした: " + e.Message);
                    // 次の周期でやり直す（_published は更新しない）
                }
            }

            private void Drain()
            {
                if (!System.IO.Directory.Exists(_requests)) return;

                string[] files;
                try
                {
                    files = System.IO.Directory.GetFiles(_requests, "*.json");
                }
                catch (Exception e)
                {
                    WarnOnce("*", "依頼箱を読めませんでした: " + e.Message);
                    return;
                }

                var names = new List<string>(files.Length);
                foreach (var file in files) names.Add(Path.GetFileName(file));

                foreach (var name in MascotRequest.OrderedRequestNames(names))
                {
                    var path = Path.Combine(_requests, name);
                    string text;
                    try
                    {
                        text = File.ReadAllText(path);
                        File.Delete(path);
                    }
                    catch (Exception e)
                    {
                        WarnOnce(name, $"依頼 {name} を読めない・消せません: {e.Message}");
                        continue;
                    }

                    if (!MascotRequest.TryParse(text, out var request, out var reason))
                    {
                        Debug.LogWarning($"[Mascot] 依頼 {name} を捨てました: {reason}");
                        continue;
                    }

                    Execute(request);
                }
            }

            private void WarnOnce(string name, string message)
            {
                if (_warned.Add(name)) Debug.LogWarning("[Mascot] " + message);
            }

            private void Execute(MascotRequest request)
            {
                switch (request.Type)
                {
                    case MascotRequestType.ResetWindow:
                        // ★ 先に最新の設定を読む。ChatterAgent は倍率を消してから依頼を置くので、
                        //   ポーリングを待つと古い倍率のまま既定の位置へ戻る
                        if (MascotSettingsHost.Instance != null) MascotSettingsHost.Instance.Refresh();
                        WindowGeometry.Reset();
                        break;

                    case MascotRequestType.PlayMotion:
                        PlayMotion(request.Id);
                        break;
                }
            }

            private void PlayMotion(string id)
            {
                var character = Character();
                var clips = character != null ? character.MotionClips : null;
                MotionClip clip = null;
                if (clips != null)
                {
                    foreach (var candidate in clips)
                    {
                        if (SettingsSchema.MotionPreviewId(candidate) == id)
                        {
                            clip = candidate;
                            break;
                        }
                    }
                }

                if (clip == null)
                {
                    Debug.LogWarning($"[Mascot] モーション {id} が見つかりません");
                    return;
                }

                var result = character.PreviewMotion(clip);
                Debug.Log("[Mascot] " + SettingsSchema.MotionPlayNotice(result, id, UiText.For(Application.systemLanguage)));
            }

            /// <summary>★ 別名で書いてから置き換える（→ <c>WindowGeometry.WriteState</c> と同じ）</summary>
            private static void WriteAtomically(string path, string text)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) System.IO.Directory.CreateDirectory(directory);

                var tmp = path + ".tmp";
                File.WriteAllText(tmp, text);
                if (File.Exists(path)) File.Replace(tmp, path, null);
                else File.Move(tmp, path);
            }
        }
    }
}
