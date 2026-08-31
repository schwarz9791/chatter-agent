/*
 * グローバルショートカット。
 *
 * ★ Carbon の RegisterEventHotKey を使うこと。 NSEvent の
 *   addGlobalMonitorForEvents と違い、アクセシビリティ権限のダイアログが出ない。
 *   常駐マスコットのために「入力の監視」を許可させるのは要求として重すぎる。
 *
 * ★ Carbon の仮想キーコードは物理キーの位置。 レイアウトに依らない。
 *   kVK_ANSI_M は JIS 配列でも同じ物理キーに M が刻印されているので実用上は一致するが、
 *   Dvorak などでは刻印と合わない。C# 側（HotKeySpec）の doc に書いてある。
 */
#import <Cocoa/Cocoa.h>
#import <Carbon/Carbon.h>
#import "CMNative.h"

static const OSType kCMHotKeySignature = 'CHMS';

@interface CMHotKeyEntry : NSObject
@property (nonatomic, assign) EventHotKeyRef ref;
@end

@implementation CMHotKeyEntry
@end

/* id（C# が割り当てた整数）→ EventHotKeyRef */
static NSMutableDictionary<NSNumber *, CMHotKeyEntry *> *gHotKeys = nil;
static EventHandlerRef gHandler = NULL;

static OSStatus CMHotKeyHandler(EventHandlerCallRef next, EventRef event, void *userData)
{
    (void)next;
    (void)userData;

    EventHotKeyID hotKeyId;
    OSStatus status = GetEventParameter(
        event, kEventParamDirectObject, typeEventHotKeyID,
        NULL, sizeof(hotKeyId), NULL, &hotKeyId);
    if (status != noErr) return status;
    if (hotKeyId.signature != kCMHotKeySignature) return eventNotHandledErr;

    /*
     * ★ ここから Unity API を呼ばないこと（C# 側の話）。 このハンドラは
     *   アプリのイベントターゲットから呼ばれ、Unity のプレイヤーループの外で走りうる。
     */
    CMEmitHotKey((int)hotKeyId.id);
    return noErr;
}

static bool CMEnsureHandler(void)
{
    if (gHandler != NULL) return true;

    EventTypeSpec spec;
    spec.eventClass = kEventClassKeyboard;
    spec.eventKind = kEventHotKeyPressed;

    OSStatus status = InstallApplicationEventHandler(
        &CMHotKeyHandler, 1, &spec, NULL, &gHandler);
    if (status != noErr) {
        CMEmitLog([[NSString stringWithFormat:
            @"ホットキーのハンドラを入れられませんでした: %d", (int)status] UTF8String]);
        gHandler = NULL;
        return false;
    }
    return true;
}

int CM_HotKeyRegister(int id, unsigned int keyCode, unsigned int modifiers)
{
    /*
     * ★ 修飾キー無しを受け付けないこと。 単独のキーを登録すると、そのキーが
     *   どのアプリでも入力できなくなる。C# 側（HotKeySpec）でも弾いているが、
     *   ABI を直接叩かれても壊れないよう二重にしておく。
     */
    if (modifiers == 0) return paramErr;

    __block int result = noErr;
    CMRunOnMain(^{
        if (!CMEnsureHandler()) {
            result = eventInternalErr;
            return;
        }
        if (gHotKeys == nil) gHotKeys = [NSMutableDictionary dictionary];

        /* 同じ id の登録が残っていれば先に外す（冪等にするため） */
        CM_HotKeyUnregister(id);

        EventHotKeyID hotKeyId;
        hotKeyId.signature = kCMHotKeySignature;
        hotKeyId.id = (UInt32)id;

        EventHotKeyRef ref = NULL;
        OSStatus status = RegisterEventHotKey(
            (UInt32)keyCode, (UInt32)modifiers, hotKeyId,
            GetApplicationEventTarget(), 0, &ref);
        if (status != noErr || ref == NULL) {
            /* -9878 = eventHotKeyExistsErr。他のアプリが同じ組み合わせを取っている */
            result = (status != noErr) ? (int)status : (int)eventInternalErr;
            return;
        }

        CMHotKeyEntry *entry = [[CMHotKeyEntry alloc] init];
        entry.ref = ref;
        gHotKeys[@(id)] = entry;
    });
    return result;
}

void CM_HotKeyUnregister(int id)
{
    CMRunOnMain(^{
        if (gHotKeys == nil) return;
        CMHotKeyEntry *entry = gHotKeys[@(id)];
        if (entry == nil) return;

        UnregisterEventHotKey(entry.ref);
        [gHotKeys removeObjectForKey:@(id)];
    });
}
