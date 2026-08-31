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

/* --- プラグインの内部で共有するもの（C# からは呼ばない） --- */

/* AppKit を触る処理を main thread に載せる。既に main なら直接走る */
void CMRunOnMain(void (^block)(void));

/* { "type": ..., "key": ... } を投げる。key が NULL なら省略する */
void CMEmitEvent(const char* type, const char* key);

/* { "type": "hotkey", "id": <id> } を投げる */
void CMEmitHotKey(int hotKeyId);

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
