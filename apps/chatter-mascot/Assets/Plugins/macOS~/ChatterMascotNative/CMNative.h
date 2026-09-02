/*
 * chatter-mascot の macOS 常駐まわり（#75）を担うネイティブプラグインの ABI。
 *
 * ★ Unity 単体では作れないものだけをここに置く:
 *     - NSStatusItem / NSMenu       … UniWindowController にも API が無い
 *     - グローバルショートカット      … Input System はフォーカスを持つときしか受け取らない
 *     - NSApplicationActivationPolicy … Dock 非表示の実行時切り替え
 *
 * ★ コールバックは1本に統一してある。理由が2つ:
 *     (1) リバース P/Invoke のデリゲートは C# 側で GC から守り続ける必要があり、
 *         本数が増えるほど保持し忘れが増える。
 *     (2) 「main thread かどうか」の保証を ObjC 側1箇所に書ける。
 *
 * ★ メニューのキーをこのプラグインに書かないこと。 JSON を読んで NSMenuItem を組み、
 *   representedObject に key を載せ、押されたらそれを返すだけにする。項目の追加・
 *   並び替え・ラベルが C# だけの変更で済むことが、この作りを選んだ理由そのもの。
 */
#ifndef CHATTER_MASCOT_NATIVE_H
#define CHATTER_MASCOT_NATIVE_H

#include <stdbool.h>

#ifdef __cplusplus
extern "C" {
#endif

/*
 * ★ 公開するものに必ず付けること。 ビルドは -fvisibility=hidden で通しているので、
 *   付け忘れたシンボルは .bundle から見えず、C# 側では
 *   EntryPointNotFoundException（DllNotFoundException ではない）になる。
 */
#define CM_EXPORT __attribute__((visibility("default")))

/* eventJson は呼び出しの間だけ有効。受け取り側でコピーすること */
typedef void (*CM_EventCallback)(const char* eventJson);

/*
 * ★ 終了処理では CM_SetEventCallback(NULL) を CM_Shutdown() より先に呼ぶこと。
 *   逆だと、終了中の menu action がもう生きていない Mono ドメインを叩く。
 */
CM_EXPORT void CM_SetEventCallback(CM_EventCallback cb);

CM_EXPORT bool        CM_Initialize(void);   /* 冪等 */
CM_EXPORT void        CM_Shutdown(void);
CM_EXPORT const char* CM_Version(void);      /* 静的な文字列。呼び出し側は解放しない */

/* 0 = regular（Dock に出る） / 1 = accessory（出ない） */
CM_EXPORT void CM_SetActivationPolicy(int policy);

/*
 * ★★ 結果を返す関数は「メインスレッドから呼ぶ」ことが契約。
 *   AppKit を触る処理は main queue に載せるが、メインでないときの CMRunOnMain は
 *   dispatch_async（デッドロック回避のため意図的）なので、**ブロックが走る前に関数が戻る**。
 *   そこで初期値を返すと「登録できていないのに成功」を騙ることになるため、
 *   非メインからの呼び出しは**明示的に失敗**にして CMEmitLog で理由を残す。
 */
CM_EXPORT bool CM_StatusItemShow(const char* menuJson);
CM_EXPORT bool CM_StatusItemUpdate(const char* menuJson);  /* チェック状態・ラベルの差し替え */
CM_EXPORT void CM_StatusItemHide(void);

/*
 * 0 = 成功。失敗したら OSStatus をそのまま返す（-9878 = 他のアプリが同じ組み合わせを取っている）。
 * ★ id は C# が割り当てる不透明な整数で、押されたときそのまま返る。
 *   ここで key 文字列を扱わないのは、上の「キーを書かない」規律を hotkey にも通すため。
 */
CM_EXPORT int  CM_HotKeyRegister(int id, unsigned int keyCode, unsigned int modifiers);
CM_EXPORT void CM_HotKeyUnregister(int id);

/*
 * パネル（#76）。schemaJson は SettingsPanelJson.Write が作ったもの。
 *
 * ★★ このパネルに設定のキーもラベルも1つも書かないこと。 kind を見てビューを組み、
 *   操作されたら key と value を返すだけ。ボタンの文字（「記録」など）まで JSON の
 *   strings で受け取るのは、そこだけ例外にすると必ず増えるため。
 *
 * ★★ nonactivatingPanel にしないこと。 LSUIElement のアプリはキーウィンドウを
 *   取りづらく、ショートカットの記録にはキー入力が要る。Show のときに
 *   [NSApp activateIgnoringOtherApps:YES] を呼ぶ。実機では
 *   「スライダーは動くが記録が始まらない」という形で出る。
 *
 * ★ panelId で複数枚を同じレンダラで描く（0 = 設定 / 1 = このアプリについて）。
 *   2枚目のために ObjC を書き足さないための引数。範囲外は無視する。
 */
CM_EXPORT bool CM_PanelShow(int panelId, const char* schemaJson);
CM_EXPORT bool CM_PanelUpdate(int panelId, const char* schemaJson);
CM_EXPORT void CM_PanelHide(int panelId);
CM_EXPORT bool CM_PanelIsVisible(int panelId);

/*
 * ファイル選択（#76）。optionsJson は {"key":…,"title":…,"message":…,"button":…,"extensions":[…]}。
 *
 * ★★ UniWindowController の FilePanel を使わないこと。 あちらは NSOpenPanel の
 *   allowedContentTypes に UTType(tag:"vrm", tagClass:.filenameExtension) を渡すが、
 *   .vrm はシステムに登録された UTI を持たないので dynamic UTType になり、
 *   **拡張子が一致してもグレーアウトする**（バイナリの逆アセンブルで確認）。
 *   ここでは allowedContentTypes を使わず、panel:shouldEnableURL: で拡張子を見る。
 *
 * ★ 拡張子もタイトルも C# から渡す（ネイティブに "vrm" を書かない）。
 * ★ 選ばれたら CMEmitSetting(key, パス) を投げる。取り消しなら何も投げない。
 */
CM_EXPORT bool CM_OpenFilePanel(const char* optionsJson);

/*
 * 確認ダイアログ（#76）。optionsJson は {"title":…,"message":…,"ok":…,"cancel":…,"destructive":bool}。
 * OK が押されたら true。
 *
 * ★ 取り消せない操作（ファイルの削除）の前に挟む。文言は C# から渡す。
 * ★ runModal はメインスレッドを止める。結果を返す関数なのでメインスレッド契約に従う。
 */
CM_EXPORT bool CM_Confirm(const char* optionsJson);

/* --- プラグインの内部で共有するもの（C# からは呼ばない） --- */

/* AppKit を触る処理を main thread に載せる。既に main なら直接走る */
void CMRunOnMain(void (^block)(void));

/* 結果を返す関数が「いま同期で走れるか」を判定する（→ 上の契約） */
bool CMIsMainThread(void);

/* { "type": ..., "key": ... } を投げる。key が NULL なら省略する */
void CMEmitEvent(const char* type, const char* key);

/* { "type": "setting", "key": ..., "value": ... } を投げる（#76） */
void CMEmitSetting(const char* key, const char* value);

/* { "type": "hotkey", "id": <id> } を投げる */
void CMEmitHotKey(int hotKeyId);

/*
 * { "type": "panel", "id": <panelId>, "state": ... } を投げる（#76）。
 *
 * ★★ 赤いボタンで閉じたことは、これでしか C# に届かない。 CM_PanelHide（orderOut:）は
 *   自分で閉じる経路なので通知が要らないが、ユーザーが閉じる経路は誰も知らないまま
 *   「開いている」状態が C# 側に残る（症状は「数分前の注記が、いま起きたことのように出る」）。
 * ★ setting に相乗りさせないこと。 あちらは「設定のキー」を運ぶ口で、
 *   ObjC に設定のキーを書かないという規律がある。これは menu / hotkey / log と同じ
 *   プロトコルの語彙なので、型を1つ足す方に倒す。
 */
void CMEmitPanel(int panelId, const char* state);

/*
 * ★ NSLog を使わないこと。 Unity の Player.log には入らないので、
 *   ビルドした .app で起きたことが**どこにも残らない**。C# 側へ流せば
 *   [Native] 付きで Player.log に出て、scripts の grep も通る。
 *   ★ コールバックが付く前（CM_SetEventCallback より前）のものは捨てられる。
 */
void CMEmitLog(const char* message);

#ifdef __cplusplus
}
#endif
#endif /* CHATTER_MASCOT_NATIVE_H */
